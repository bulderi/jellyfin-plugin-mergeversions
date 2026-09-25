using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable, IIncrementalMergeProcessor
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<MergeVersionsManager> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IVideoVersions _videoVersions;
        private readonly SemaphoreSlim _mergeGate = new(1, 1);

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem,
            IVideoVersions videoVersions
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _videoVersions = videoVersions;
        }

        public Task MergeMoviesAsync(
            IProgress<double> progress,
            ClaimsPrincipal user = null,
            CancellationToken cancellationToken = default)
            => RunExclusiveAsync(
                () => MergeMoviesCoreAsync(progress, user, cancellationToken),
                cancellationToken);

        private async Task MergeMoviesCoreAsync(
            IProgress<double> progress,
            ClaimsPrincipal user,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated movies");

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(group => group.Count() > 1 && NeedsMerge(group))
                .ToList();

            var current = 0;
            foreach (var movies in duplicateMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = movies.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(movies.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateMovies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public Task SplitMoviesAsync(
            IProgress<double> progress,
            ClaimsPrincipal user = null,
            CancellationToken cancellationToken = default)
            => RunExclusiveAsync(
                () => SplitMoviesCoreAsync(progress, user, cancellationToken),
                cancellationToken);

        private async Task SplitMoviesCoreAsync(
            IProgress<double> progress,
            ClaimsPrincipal user,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movies = GetMoviesFromLibrary();
            var current = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {Name} ({Year})", movie.Name, movie.ProductionYear);
                await _videoVersions.SplitAsync(movie.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)movies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public Task MergeEpisodesAsync(
            IProgress<double> progress,
            ClaimsPrincipal user = null,
            CancellationToken cancellationToken = default)
            => RunExclusiveAsync(
                () => MergeEpisodesCoreAsync(progress, user, cancellationToken),
                cancellationToken);

        private async Task MergeEpisodesCoreAsync(
            IProgress<double> progress,
            ClaimsPrincipal user,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated episodes");

            var episodes = GetEpisodesFromLibrary();
            var duplicateEpisodes = episodes
                .GroupBy(GetEpisodeMergeKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1 && NeedsMerge(group))
                .ToList();

            _logger.LogInformation(
                "Found {EpisodeCount} episodes and {DuplicateGroupCount} duplicate episode groups",
                episodes.Count,
                duplicateEpisodes.Count);

            var current = 0;
            foreach (var episodeGroup in duplicateEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = episodeGroup.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(episodeGroup.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateEpisodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        bool IIncrementalMergeProcessor.TryCreateIncrementalTarget(
            BaseItem item,
            out IncrementalMergeTarget target)
        {
            target = null;
            if (!IsEligible(item))
            {
                return false;
            }

            if (item is Movie movie
                && movie.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                && !string.IsNullOrWhiteSpace(tmdbId))
            {
                target = new IncrementalMergeTarget(
                    $"movie:provider:Tmdb:{tmdbId}",
                    BaseItemKind.Movie,
                    "Tmdb",
                    tmdbId);
                return true;
            }

            if (item is Episode episode)
            {
                target = CreateEpisodeTarget(episode);
                return true;
            }

            return false;
        }

        Task<int> IIncrementalMergeProcessor.MergeIncrementalBatchAsync(
            IReadOnlyCollection<IncrementalMergeTarget> targets,
            CancellationToken cancellationToken)
            => RunExclusiveAsync(
                () => MergeIncrementalBatchCoreAsync(targets, cancellationToken),
                cancellationToken);

        private async Task<int> MergeIncrementalBatchCoreAsync(
            IReadOnlyCollection<IncrementalMergeTarget> targets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targets.Count == 0)
            {
                return 0;
            }

            var targetKeys = targets
                .Select(target => target.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = new Dictionary<Guid, Video>();

            var providerTargets = targets
                .Where(target => target.ProviderName is not null && target.ProviderValue is not null)
                .ToList();
            if (providerTargets.Count > 0)
            {
                var providerIds = providerTargets
                    .GroupBy(target => target.ProviderName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(target => target.ProviderValue!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        StringComparer.OrdinalIgnoreCase);

                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = providerTargets
                        .Select(target => target.ItemType)
                        .Distinct()
                        .ToArray(),
                    HasAnyProviderIds = providerIds,
                    IsVirtualItem = false,
                    Recursive = true
                };

                foreach (var video in _libraryManager.GetItemList(query).OfType<Video>().Where(IsEligible))
                {
                    candidates[video.Id] = video;
                }
            }

            foreach (var target in targets.Where(target => target.ProviderName is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = [target.ItemType],
                    IsVirtualItem = false,
                    Recursive = true,
                    SeriesPresentationUniqueKey = target.SeriesPresentationUniqueKey,
                    ParentIndexNumber = target.ParentIndexNumber,
                    IndexNumber = target.IndexNumber,
                    Name = target.ParentIndexNumber.HasValue && target.IndexNumber.HasValue
                        ? null
                        : target.ItemName
                };

                foreach (var video in _libraryManager.GetItemList(query).OfType<Video>().Where(IsEligible))
                {
                    candidates[video.Id] = video;
                }
            }

            var groups = candidates.Values
                .Select(video => (Video: video, Target: CreateTarget(video)))
                .Where(entry => entry.Target is not null && targetKeys.Contains(entry.Target.Key))
                .GroupBy(entry => entry.Target!.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Select(entry => entry.Video).ToList())
                .Where(group => group.Count > 1 && NeedsMerge(group))
                .ToList();

            var mergedGroups = 0;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _videoVersions
                    .MergeAsync(group.Select(video => video.Id).ToArray(), null, cancellationToken)
                    .ConfigureAwait(false);
                mergedGroups++;
            }

            return mergedGroups;
        }

        public Task SplitEpisodesAsync(
            IProgress<double> progress,
            ClaimsPrincipal user = null,
            CancellationToken cancellationToken = default)
            => RunExclusiveAsync(
                () => SplitEpisodesCoreAsync(progress, user, cancellationToken),
                cancellationToken);

        private async Task SplitEpisodesCoreAsync(
            IProgress<double> progress,
            ClaimsPrincipal user,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {EpisodeNumber} ({SeriesName})", episode.IndexNumber, episode.SeriesName);
                await _videoVersions.SplitAsync(episode.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)episodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .OfType<Movie>()
                .Where(movie => movie.ProviderIds.ContainsKey("Tmdb"))
                .Where(IsEligible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .OfType<Episode>()
                .Where(IsEligible)
                .ToList();
        }

        private static IncrementalMergeTarget CreateTarget(Video video)
        {
            if (video is Movie movie
                && movie.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                && !string.IsNullOrWhiteSpace(tmdbId))
            {
                return new IncrementalMergeTarget(
                    $"movie:provider:Tmdb:{tmdbId}",
                    BaseItemKind.Movie,
                    "Tmdb",
                    tmdbId);
            }

            return video is Episode episode ? CreateEpisodeTarget(episode) : null;
        }

        private static IncrementalMergeTarget CreateEpisodeTarget(Episode episode)
        {
            foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
            {
                if (episode.ProviderIds.TryGetValue(provider, out var providerId)
                    && !string.IsNullOrWhiteSpace(providerId))
                {
                    return new IncrementalMergeTarget(
                        $"episode:provider:{provider}:{providerId}",
                        BaseItemKind.Episode,
                        provider,
                        providerId);
                }
            }

            if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
            {
                return new IncrementalMergeTarget(
                    $"episode:number:{episode.SeriesName}:{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd}",
                    BaseItemKind.Episode,
                    SeriesPresentationUniqueKey: episode.SeriesPresentationUniqueKey,
                    ItemName: episode.Name,
                    ParentIndexNumber: episode.ParentIndexNumber,
                    IndexNumber: episode.IndexNumber);
            }

            return new IncrementalMergeTarget(
                $"episode:title:{episode.SeriesName}:{episode.SeasonName}:{episode.Name}:{episode.ProductionYear}",
                BaseItemKind.Episode,
                SeriesPresentationUniqueKey: episode.SeriesPresentationUniqueKey,
                ItemName: episode.Name);
        }

        private static string GetEpisodeMergeKey(Episode episode)
            => CreateEpisodeTarget(episode).Key;

        private static bool NeedsMerge(IEnumerable<Video> versions)
        {
            var items = versions.ToList();
            var primaries = items.Where(item => !item.PrimaryVersionId.HasValue).ToList();
            if (primaries.Count != 1)
            {
                return true;
            }

            var primary = primaries[0];
            var expectedAlternates = items
                .Where(item => item.Id != primary.Id)
                .Select(item => item.Id)
                .ToHashSet();
            var linkedAlternates = primary.LinkedAlternateVersions
                .Where(link => link.ItemId.HasValue)
                .Select(link => link.ItemId!.Value)
                .ToHashSet();

            return !expectedAlternates.SetEquals(linkedAlternates)
                || items.Any(item => item.Id != primary.Id && item.PrimaryVersionId != primary.Id);
        }

        private bool IsEligible(BaseItem item)
        {
            if (IsInInactiveLibrary(item) || IsInExcludedLibrary(item))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
            return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                   && Plugin.Instance.PluginConfiguration.LocationsExcluded
                       .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var virtualFolders = _libraryManager.GetVirtualFolders();

            return !virtualFolders
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            await _mergeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                _mergeGate.Release();
            }
        }

        private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            await _mergeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _mergeGate.Release();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _mergeGate.Dispose();
            }
        }
    }
}
