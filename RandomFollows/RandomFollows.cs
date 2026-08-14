using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomFollows;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks an arbitrary candidate/delay, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomFollows : IASF, IBotConnection, IGitHubPluginUpdates {
	private const ushort DefaultCandidatePoolCacheHours = 6;
	private const byte DefaultCuratorsTargetMaxCount = 5;
	private const byte DefaultCuratorsTargetMinCount = 0;
	private const byte DefaultGamesTargetMaxCount = 10;
	private const byte DefaultGamesTargetMinCount = 3;
	private const ushort DefaultMaxDelayInMinutes = 1440;
	private const ushort DefaultMinDelayInMinutes = 360;

	// Steam's public storefront widgets - the same pool every bot picks from, so it's cached once for the whole process rather than per bot
	private static readonly Uri FeaturedCategoriesRequest = new("https://store.steampowered.com/api/featuredcategories?cc=us&l=english");
	private static readonly Uri SteamStoreURL = new("https://store.steampowered.com");

	private readonly ConcurrentDictionary<string, int> BotCuratorsFollowedCount = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> BotCuratorsTarget = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, HashSet<ulong>> BotFollowedCuratorIDs = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, HashSet<uint>> BotFollowedGameIDs = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> BotGamesFollowedCount = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, int> BotGamesTarget = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> BotNoEligibleGamesWarned = new(StringComparer.Ordinal);

	// AppIDs the store confirmed aren't actual games (DLC/soundtrack/tool that slipped into New Releases or Specials); shared across bots, skipped for the rest of the process
	private readonly ConcurrentDictionary<uint, byte> RejectedAppIDs = new();

	// Serializes concurrent refreshes of CandidatePoolCache so multiple bots hitting an expired cache at once don't all fetch it in parallel
	private readonly SemaphoreSlim CandidatePoolLock = new(1, 1);

	private (DateTime FetchedAt, uint[] AppIDs) CandidatePoolCache;
	private ushort CandidatePoolCacheHours = DefaultCandidatePoolCacheHours;
	private ulong[] CuratorClanIDs = [];
	private byte CuratorsTargetMaxCount = DefaultCuratorsTargetMaxCount;
	private byte CuratorsTargetMinCount = DefaultCuratorsTargetMinCount;
	private bool Enabled;
	private bool FollowCurators;
	private bool FollowGames = true;
	private byte GamesTargetMaxCount = DefaultGamesTargetMaxCount;
	private byte GamesTargetMinCount = DefaultGamesTargetMinCount;
	private ushort MaxDelayInMinutes = DefaultMaxDelayInMinutes;
	private ushort MinDelayInMinutes = DefaultMinDelayInMinutes;

	public string Name => nameof(RandomFollows);
	public string RepositoryName => "buddymurdock/ASF-RandomFollows";
	public Version Version => typeof(RandomFollows).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomFollowsEnabled / RandomFollowsMinDelayMinutes / RandomFollowsMaxDelayMinutes / RandomFollowsFollowGames /
	// RandomFollowsGamesTargetMinCount / RandomFollowsGamesTargetMaxCount / RandomFollowsCandidatePoolCacheHours /
	// RandomFollowsFollowCurators / RandomFollowsCuratorClanIDs / RandomFollowsCuratorsTargetMinCount /
	// RandomFollowsCuratorsTargetMaxCount from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		HashSet<ulong> parsedCuratorClanIDs = [];

		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomFollows)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomFollows)}MinDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelay) && (minDelay > 0):
						MinDelayInMinutes = minDelay;

						break;
					case $"{nameof(RandomFollows)}MaxDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelay) && (maxDelay > 0):
						MaxDelayInMinutes = maxDelay;

						break;
					case $"{nameof(RandomFollows)}FollowGames" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						FollowGames = configValue.GetBoolean();

						break;
					case $"{nameof(RandomFollows)}GamesTargetMinCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte gamesTargetMin):
						GamesTargetMinCount = gamesTargetMin;

						break;
					case $"{nameof(RandomFollows)}GamesTargetMaxCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte gamesTargetMax):
						GamesTargetMaxCount = gamesTargetMax;

						break;
					case $"{nameof(RandomFollows)}CandidatePoolCacheHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort cacheHours) && (cacheHours > 0):
						CandidatePoolCacheHours = cacheHours;

						break;
					case $"{nameof(RandomFollows)}FollowCurators" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						FollowCurators = configValue.GetBoolean();

						break;
					case $"{nameof(RandomFollows)}CuratorClanIDs" when configValue.ValueKind == JsonValueKind.Array:
						AddParsedClanIDs(configValue, parsedCuratorClanIDs);

						break;
					case $"{nameof(RandomFollows)}CuratorsTargetMinCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte curatorsTargetMin):
						CuratorsTargetMinCount = curatorsTargetMin;

						break;
					case $"{nameof(RandomFollows)}CuratorsTargetMaxCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte curatorsTargetMax):
						CuratorsTargetMaxCount = curatorsTargetMax;

						break;
				}
			}
		}

		CuratorClanIDs = [.. parsedCuratorClanIDs];

		if (MinDelayInMinutes > MaxDelayInMinutes) {
			(MinDelayInMinutes, MaxDelayInMinutes) = (MaxDelayInMinutes, MinDelayInMinutes);
		}

		if (GamesTargetMinCount > GamesTargetMaxCount) {
			(GamesTargetMinCount, GamesTargetMaxCount) = (GamesTargetMaxCount, GamesTargetMinCount);
		}

		if (CuratorsTargetMinCount > CuratorsTargetMaxCount) {
			(CuratorsTargetMinCount, CuratorsTargetMaxCount) = (CuratorsTargetMaxCount, CuratorsTargetMinCount);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomFollows)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		if (!FollowGames && !FollowCurators) {
			ASF.ArchiLogger.LogGenericWarning($"{Name} is enabled, but both {nameof(RandomFollows)}FollowGames and {nameof(RandomFollows)}FollowCurators are false, so there's nothing to follow.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInMinutes}-{MaxDelayInMinutes} minutes (approximate, see README). Sources: {(FollowGames ? $"games ({GamesTargetMinCount}-{GamesTargetMaxCount}/bot, from Steam's New Releases/Specials)" : null)}{((FollowGames && FollowCurators) ? " + " : null)}{(FollowCurators ? $"curators ({CuratorsTargetMinCount}-{CuratorsTargetMaxCount}/bot, from {CuratorClanIDs.Length} configured)" : null)}.");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			await cts.CancelAsync().ConfigureAwait(false);
			cts.Dispose();
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		if (!Enabled || (!FollowGames && !FollowCurators)) {
			return Task.CompletedTask;
		}

		CancellationTokenSource cts = new();

		if (!BotLoops.TryAdd(bot.BotName, cts)) {
			// A loop for this bot is already running, nothing to do
			cts.Dispose();

			return Task.CompletedTask;
		}

		Utilities.InBackground(() => BotFollowLoopAsync(bot, cts.Token), true);

		return Task.CompletedTask;
	}

	private async Task BotFollowLoopAsync(Bot bot, CancellationToken cancellationToken) {
		int gamesTarget = FollowGames ? BotGamesTarget.GetOrAdd(bot.BotName, _ => GamesTargetMinCount == GamesTargetMaxCount ? GamesTargetMinCount : Random.Shared.Next(GamesTargetMinCount, GamesTargetMaxCount + 1)) : 0;
		int curatorsTarget = FollowCurators ? BotCuratorsTarget.GetOrAdd(bot.BotName, _ => GetRandomCuratorsTarget()) : 0;

		while (!cancellationToken.IsCancellationRequested) {
			bool gamesDone = !FollowGames || (BotGamesFollowedCount.GetOrAdd(bot.BotName, 0) >= gamesTarget);
			bool curatorsDone = !FollowCurators || (BotCuratorsFollowedCount.GetOrAdd(bot.BotName, 0) >= curatorsTarget);

			if (gamesDone && curatorsDone) {
				// This bot reached its randomly assigned target(s) for the process' lifetime; nothing left to do
				return;
			}

			TimeSpan delay = GetRandomDelay(MinDelayInMinutes, MaxDelayInMinutes);

			try {
				await LongDelayAsync(delay, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (cancellationToken.IsCancellationRequested || !bot.IsConnectedAndLoggedOn) {
				break;
			}

			try {
				await TryFollowSingleAsync(bot, gamesTarget, curatorsTarget).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Task.Delay's underlying timer caps out at ~49.7 days (uint.MaxValue-1 ms) - a delay past that
	// throws ArgumentOutOfRangeException synchronously, which would go unhandled here and crash the
	// entire ASF process via OnUnobservedTaskException (this exact bug hit RandomNickname/RandomProfileAvatar/
	// RandomProfileBackground in production). Chunking sidesteps the limit for arbitrarily long delays.
	private static async Task LongDelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
		TimeSpan chunk = TimeSpan.FromDays(1);

		while (delay > chunk) {
			await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
			delay -= chunk;
		}

		if (delay > TimeSpan.Zero) {
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	// Real people don't wait a uniformly random amount of time between actions - intervals tend
	// to cluster around a typical gap with occasional much shorter/longer ones (bursty/heavy-tailed),
	// not spread flat across [min, max]. Log-normal captures that: min/max become the ~5th/95th
	// percentiles rather than hard bounds, with sqrt(min*max) as the median.
	// z is clamped before use because extreme (min, max) ratios produce a large sigma - an un-clamped
	// Box-Muller tail can drive Math.Exp()/TimeSpan construction into Infinity/OverflowException, the
	// same failure class LongDelayAsync above was written to fix. The final Math.Clamp is a second,
	// independent safety net on the result itself, keeping delays (and LongDelayAsync's chunking loop)
	// bounded to something sane even for pathological configs.
	private static TimeSpan GetRandomDelay(ushort minMinutes, ushort maxMinutes) {
		if (minMinutes == maxMinutes) {
			return TimeSpan.FromMinutes(minMinutes);
		}

		double median = Math.Sqrt((double) minMinutes * maxMinutes);
		double sigma = Math.Log((double) maxMinutes / minMinutes) / (2 * 1.645);

		double u1 = 1.0 - Random.Shared.NextDouble();
		double u2 = Random.Shared.NextDouble();
		double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

		z = Math.Clamp(z, -3.5, 3.5);

		double minutes = median * Math.Exp(sigma * z);
		minutes = Math.Clamp(minutes, minMinutes / 10.0, maxMinutes * 5.0);

		return TimeSpan.FromMinutes(minutes);
	}

	private int GetRandomCuratorsTarget() {
		int min = Math.Min(CuratorsTargetMinCount, CuratorClanIDs.Length);
		int max = Math.Min(CuratorsTargetMaxCount, CuratorClanIDs.Length);

		return min == max ? min : Random.Shared.Next(min, max + 1);
	}

	// Randomizes which enabled, not-yet-complete source is tried first each tick, falling back to the
	// other one if the first has nothing to offer this time - avoids a fixed "always games first" order
	// being itself a detectable pattern, the same reasoning already applied in RandomBotFriends.
	private async Task TryFollowSingleAsync(Bot bot, int gamesTarget, int curatorsTarget) {
		bool gamesEligible = FollowGames && (BotGamesFollowedCount.GetOrAdd(bot.BotName, 0) < gamesTarget);
		bool curatorsEligible = FollowCurators && (BotCuratorsFollowedCount.GetOrAdd(bot.BotName, 0) < curatorsTarget);

		if (!gamesEligible && !curatorsEligible) {
			return;
		}

		bool tryGamesFirst = !curatorsEligible || (gamesEligible && (Random.Shared.Next(2) == 0));

		if (tryGamesFirst) {
			if (await TryFollowRandomGameAsync(bot).ConfigureAwait(false)) {
				return;
			}

			if (curatorsEligible) {
				await TryFollowRandomCuratorAsync(bot).ConfigureAwait(false);
			}
		} else {
			if (await TryFollowRandomCuratorAsync(bot).ConfigureAwait(false)) {
				return;
			}

			if (gamesEligible) {
				await TryFollowRandomGameAsync(bot).ConfigureAwait(false);
			}
		}
	}

	private async Task<bool> TryFollowRandomGameAsync(Bot bot) {
		uint[] pool = await GetGameCandidatePoolAsync(bot).ConfigureAwait(false);

		if (pool.Length == 0) {
			return false;
		}

		HashSet<uint> alreadyFollowed = BotFollowedGameIDs.GetOrAdd(bot.BotName, static _ => []);

		Dictionary<uint, string>? ownedGames = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);

		List<uint> eligible = [
			.. pool.Where(
				appID => !RejectedAppIDs.ContainsKey(appID) &&
					!alreadyFollowed.Contains(appID) &&
					((ownedGames == null) || !ownedGames.ContainsKey(appID))
			)
		];

		if (eligible.Count == 0) {
			if (BotNoEligibleGamesWarned.TryAdd(bot.BotName, true)) {
				bot.ArchiLogger.LogGenericInfo($"{Name}: no eligible game-follow candidates left in the current pool for this bot (all owned, already followed, or rejected); will keep retrying as the pool changes.");
			}

			return false;
		}

		BotNoEligibleGamesWarned.TryRemove(bot.BotName, out _);

		uint appID = eligible[Random.Shared.Next(eligible.Count)];

		if (!await IsRealGameAsync(bot, appID).ConfigureAwait(false)) {
			RejectedAppIDs.TryAdd(appID, 0);

			return false;
		}

		bool success = await FollowGameAsync(bot, appID).ConfigureAwait(false);

		if (success) {
			alreadyFollowed.Add(appID);

			int followedSoFar = BotGamesFollowedCount.AddOrUpdate(bot.BotName, 1, static (_, count) => count + 1);

			bot.ArchiLogger.LogGenericInfo($"Randomly followed app {appID} ({followedSoFar}/{BotGamesTarget[bot.BotName]}).");
		} else {
			bot.ArchiLogger.LogGenericWarning($"Failed to follow app {appID}.");
		}

		return true;
	}

	private async Task<bool> TryFollowRandomCuratorAsync(Bot bot) {
		if (CuratorClanIDs.Length == 0) {
			return false;
		}

		HashSet<ulong> alreadyFollowed = BotFollowedCuratorIDs.GetOrAdd(bot.BotName, static _ => []);

		List<ulong> eligible = [.. CuratorClanIDs.Where(clanID => !alreadyFollowed.Contains(clanID))];

		if (eligible.Count == 0) {
			return false;
		}

		ulong clanID = eligible[Random.Shared.Next(eligible.Count)];

		bool success = await FollowCuratorAsync(bot, clanID).ConfigureAwait(false);

		if (success) {
			alreadyFollowed.Add(clanID);

			int followedSoFar = BotCuratorsFollowedCount.AddOrUpdate(bot.BotName, 1, static (_, count) => count + 1);

			bot.ArchiLogger.LogGenericInfo($"Randomly followed curator {clanID} ({followedSoFar}/{BotCuratorsTarget[bot.BotName]}).");
		} else {
			bot.ArchiLogger.LogGenericWarning($"Failed to follow curator {clanID}.");
		}

		return true;
	}

	private async Task<uint[]> GetGameCandidatePoolAsync(Bot bot) {
		(DateTime FetchedAt, uint[] AppIDs) cached = CandidatePoolCache;

		if ((cached.AppIDs.Length > 0) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(CandidatePoolCacheHours))) {
			return cached.AppIDs;
		}

		await CandidatePoolLock.WaitAsync().ConfigureAwait(false);

		try {
			// Re-check after acquiring the lock - another bot may have already refreshed it while we were waiting
			cached = CandidatePoolCache;

			if ((cached.AppIDs.Length > 0) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(CandidatePoolCacheHours))) {
				return cached.AppIDs;
			}

			ObjectResponse<FeaturedCategoriesResponse>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<FeaturedCategoriesResponse>(FeaturedCategoriesRequest).ConfigureAwait(false);

			HashSet<uint> appIDs = [];

			foreach (uint appID in (response?.Content?.NewReleases?.Items ?? []).Select(static item => item.ID)) {
				appIDs.Add(appID);
			}

			foreach (uint appID in (response?.Content?.Specials?.Items ?? []).Select(static item => item.ID)) {
				appIDs.Add(appID);
			}

			if (appIDs.Count == 0) {
				// The fetch failed or returned nothing useful - keep serving whatever we had before (possibly empty) rather than blocking every bot on a hard failure
				return CandidatePoolCache.AppIDs;
			}

			CandidatePoolCache = (DateTime.UtcNow, [.. appIDs]);

			return CandidatePoolCache.AppIDs;
		} finally {
			CandidatePoolLock.Release();
		}
	}

	private static async Task<bool> IsRealGameAsync(Bot bot, uint appID) {
		Uri request = new($"https://store.steampowered.com/api/appdetails?appids={appID}&cc=us&l=english");
		ObjectResponse<Dictionary<string, AppDetailsEntry>>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<Dictionary<string, AppDetailsEntry>>(request).ConfigureAwait(false);

		if ((response?.Content == null) || !response.Content.TryGetValue(appID.ToString(CultureInfo.InvariantCulture), out AppDetailsEntry? entry) || !entry.Success || (entry.Data == null)) {
			return false;
		}

		return string.Equals(entry.Data.Type, "game", StringComparison.OrdinalIgnoreCase);
	}

	// store.steampowered.com/explore/followgame/ is Steam's own "Follow" button endpoint on a game's store
	// page - lighter-weight than a wishlist entry (no purchase-intent signal), just a discovery/notification
	// subscription. Verified against chr233/ASFEnhance's already-working FollowGame implementation, not guessed.
	private static async Task<bool> FollowGameAsync(Bot bot, uint appID) {
		Uri request = new(SteamStoreURL, "/explore/followgame/");
		Uri referer = new(SteamStoreURL, $"/app/{appID}");

		Dictionary<string, string> data = new(StringComparer.Ordinal) {
			{ "appid", appID.ToString(CultureInfo.InvariantCulture) }
		};

		HtmlDocumentResponse? response = await bot.ArchiWebHandler.UrlPostToHtmlDocumentWithSession(request, data: data, referer: referer).ConfigureAwait(false);

		return string.Equals(response?.Content?.Body?.InnerHtml, "true", StringComparison.OrdinalIgnoreCase);
	}

	// store.steampowered.com/curators/ajaxfollow is Steam's own curator-follow endpoint. Verified against
	// chr233/ASFEnhance's already-working FollowCurator implementation, not guessed.
	private static async Task<bool> FollowCuratorAsync(Bot bot, ulong clanID) {
		Uri request = new(SteamStoreURL, "/curators/ajaxfollow");
		Uri referer = new(SteamStoreURL, $"curator/{clanID}");

		Dictionary<string, string> data = new(StringComparer.Ordinal) {
			{ "clanid", clanID.ToString(CultureInfo.InvariantCulture) },
			{ "follow", "1" }
		};

		ObjectResponse<AjaxFollowResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<AjaxFollowResponse>(request, data: data, referer: referer).ConfigureAwait(false);

		return response?.Content?.Success?.Result == EResult.OK;
	}

	private static void AddParsedClanIDs(JsonElement array, HashSet<ulong> target) {
		foreach (JsonElement clanElement in array.EnumerateArray()) {
			ulong? clanID = clanElement.ValueKind switch {
				JsonValueKind.Number when clanElement.TryGetUInt64(out ulong numericID) => numericID,
				JsonValueKind.String when ulong.TryParse(clanElement.GetString(), out ulong stringID) => stringID,
				_ => null
			};

			if ((clanID is { } validClanID) && (validClanID != 0) && new SteamID(validClanID).IsClanAccount) {
				target.Add(validClanID);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid {nameof(RandomFollows)}CuratorClanIDs entry: {clanElement}.");
			}
		}
	}

	private sealed record FeaturedCategoriesResponse(
		[property: JsonPropertyName("new_releases")] FeaturedCategory? NewReleases,
		[property: JsonPropertyName("specials")] FeaturedCategory? Specials
	);

	private sealed record FeaturedCategory([property: JsonPropertyName("items")] IReadOnlyList<FeaturedItem>? Items);

	private sealed record FeaturedItem([property: JsonPropertyName("id")] uint ID);

	private sealed record AppDetailsEntry([property: JsonPropertyName("success")] bool Success, [property: JsonPropertyName("data")] AppDetailsData? Data);

	private sealed record AppDetailsData([property: JsonPropertyName("type")] string? Type);

	private sealed record AjaxFollowResponse([property: JsonPropertyName("success")] AjaxFollowResult? Success);

	private sealed record AjaxFollowResult([property: JsonPropertyName("success")] EResult Result);
}
#pragma warning restore CA5394
#pragma warning restore CA1001
#pragma warning restore CA1812
