﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using Autofac;
using SmartStore.Core.Domain.Forums;
using SmartStore.Core.Localization;
using SmartStore.Core.Logging;
using SmartStore.Core.Search;
using SmartStore.Core.Search.Facets;
using SmartStore.Services.Forums;

namespace SmartStore.Services.Search
{
    public partial class ForumSearchService : SearchServiceBase, IForumSearchService
    {
		private readonly ICommonServices _services;
		private readonly IIndexManager _indexManager;
        private readonly Lazy<IForumService> _forumService;
        private readonly ILogger _logger;
		private readonly UrlHelper _urlHelper;

		public ForumSearchService(
			ICommonServices services,
			IIndexManager indexManager,
            Lazy<IForumService> forumService,
			ILogger logger,
			UrlHelper urlHelper)
		{
			_services = services;
			_indexManager = indexManager;
            _forumService = forumService;
			_logger = logger;
			_urlHelper = urlHelper;

            T = NullLocalizer.Instance;
        }

        public Localizer T { get; set; }

        /// <summary>
        /// Bypasses the index provider and directly searches in the database
        /// </summary>
        /// <param name="searchQuery">Search query</param>
        /// <returns>Forum search result</returns>
        protected virtual ForumSearchResult SearchDirect(ForumSearchQuery searchQuery)
		{
			// Fallback to linq search.
			var linqForumSearchService = _services.Container.ResolveNamed<IForumSearchService>("linq");
			var result = linqForumSearchService.Search(searchQuery, true);
            ApplyFacetLabels(result.Facets);

            return result;
		}

        protected virtual void ApplyFacetLabels(IDictionary<string, FacetGroup> facets)
        {
            if (facets == null || !facets.Any())
            {
                return;
            }

            // Ensure that there are no duplicate customer labels.
            if (facets.TryGetValue("customerid", out var grp))
            {
                var groupedByLabel =
                    from x in grp.Facets
                    group x by x.Value.Label into g
                    select g;

                foreach (var item in groupedByLabel.Where(x => x.Count() > 1))
                {
                    foreach (var facet in item)
                    {
                        facet.Value.Label = $"{facet.Value.Value} ({facet.Value.Value.ToString()})";
                    }
                }
            }
        }

        public ForumSearchResult Search(ForumSearchQuery searchQuery, bool direct = false)
        {
			Guard.NotNull(searchQuery, nameof(searchQuery));
			Guard.NotNegative(searchQuery.Take, nameof(searchQuery.Take));

			var provider = _indexManager.GetIndexProvider("Forum");

			if (!direct && provider != null)
			{
				var indexStore = provider.GetIndexStore("Forum");
				if (indexStore.Exists)
				{
					var searchEngine = provider.GetSearchEngine(indexStore, searchQuery);
					var stepPrefix = searchEngine.GetType().Name + " - ";
					var totalCount = 0;
					string[] spellCheckerSuggestions = null;
					IEnumerable<ISearchHit> searchHits;
					Func<IList<ForumTopic>> hitsFactory = null;
                    IDictionary<string, FacetGroup> facets = null;

                    _services.EventPublisher.Publish(new ForumSearchingEvent(searchQuery));

					if (searchQuery.Take > 0)
					{
						using (_services.Chronometer.Step(stepPrefix + "Count"))
						{
							totalCount = searchEngine.Count();
							// Fix paging boundaries.
							if (searchQuery.Skip > 0 && searchQuery.Skip >= totalCount)
							{
								searchQuery.Slice((totalCount / searchQuery.Take) * searchQuery.Take, searchQuery.Take);
							}
						}

						using (_services.Chronometer.Step(stepPrefix + "Hits"))
						{
							searchHits = searchEngine.Search();
						}

						if (searchQuery.ResultFlags.HasFlag(SearchResultFlags.WithHits))
						{
							using (_services.Chronometer.Step(stepPrefix + "Collect"))
							{
                                var postIds = searchHits
                                    .Select(x => new
                                    {
                                        TopicId = x.GetInt("topicid"),
                                        PostId = x.GetInt("id")
                                    })
                                    .ToMultimap(x => x.TopicId, x => x.PostId);

                                var topicIds = postIds.Select(x => x.Key).ToArray();

                                hitsFactory = () =>
                                {
                                    var hits = _forumService.Value.GetTopicsByIds(topicIds);

                                    // Provide the id of the first hit, so that we can jump directly to the post when opening the topic.
                                    foreach (var topic in hits)
                                    {
                                        if (postIds.ContainsKey(topic.Id))
                                        {
                                            topic.FirstPostId = postIds[topic.Id].OrderBy(x => x).FirstOrDefault();
                                        }
                                    }

                                    return hits;
                                };
                            }
						}

                        if (searchQuery.ResultFlags.HasFlag(SearchResultFlags.WithFacets))
                        {
                            try
                            {
                                using (_services.Chronometer.Step(stepPrefix + "Facets"))
                                {
                                    facets = searchEngine.GetFacetMap();
                                    ApplyFacetLabels(facets);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex);
                            }
                        }
                    }

					if (searchQuery.ResultFlags.HasFlag(SearchResultFlags.WithSuggestions))
					{
						try
						{
							using (_services.Chronometer.Step(stepPrefix + "Spellcheck"))
							{
								spellCheckerSuggestions = searchEngine.CheckSpelling();
							}
						}
						catch (Exception ex)
						{
							// Spell checking should not break the search.
							_logger.Error(ex);
						}
					}

					var result = new ForumSearchResult(
						searchEngine,
						searchQuery,
						totalCount,
						hitsFactory,
						spellCheckerSuggestions,
                        facets);

					_services.EventPublisher.Publish(new ForumSearchedEvent(searchQuery, result));

					return result;
				}
				else if (searchQuery.Origin.IsCaseInsensitiveEqual("Boards/Search"))
				{
					IndexingRequiredNotification(_services, _urlHelper);
				}
			}

			return SearchDirect(searchQuery);
		}

		public IQueryable<ForumTopic> PrepareQuery(ForumSearchQuery searchQuery, IQueryable<ForumPost> baseQuery = null)
        {
			var linqForumSearchService = _services.Container.ResolveNamed<IForumSearchService>("linq");
			return linqForumSearchService.PrepareQuery(searchQuery, baseQuery);
		}
	}
}
