using System.Collections;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;

namespace FishingForecast;

/// <summary>
/// World Navigator can briefly return only the current map while its daily world
/// scan is still warming up. This tells the menu to wait and retry instead of
/// caching that incomplete result for the rest of the day.
/// </summary>
internal sealed class WorldNavigatorNotReadyException : Exception
{
    public WorldNavigatorNotReadyException(string message) : base(message) { }
}

/// <summary>
/// Optional bridge to World Navigator.
/// </summary>
internal sealed class WorldNavigatorBridge
{
    private const string ModId = "pneuma163.WorldNavigator";
    private const string ThisModId = "OpenAI.FishingForecast";

    private readonly IModHelper helper;
    private readonly IMonitor monitor;

    // World Navigator's first world/route scan can be expensive. Reuse the reachable
    // set for the rest of the in-game day while the player remains in the same map.
    // This avoids repeatedly triggering World Navigator's expensive world scan.
    // A manual refresh explicitly invalidates this cache.
    private string? cachedLocationName;
    private int cachedDay = -1;
    private Dictionary<string, int>? cachedReachable;

    private string? incompleteLocationName;
    private int incompleteDay = -1;
    private int incompleteAttempts;

    public WorldNavigatorBridge(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
    }

    public bool IsInstalled => this.helper.ModRegistry.IsLoaded(ModId);

    public void InvalidateCache()
    {
        this.cachedLocationName = null;
        this.cachedDay = -1;
        this.cachedReachable = null;
        this.incompleteLocationName = null;
        this.incompleteDay = -1;
        this.incompleteAttempts = 0;
    }

    public bool TryGetReachableLocations(out Dictionary<string, int> travelMinutes)
    {
        travelMinutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!this.IsInstalled)
            return false;

        string currentLocationName = Game1.currentLocation?.NameOrUniqueName ?? string.Empty;
        int currentDay = Game1.Date?.TotalDays ?? -1;
        if (this.cachedReachable is not null
            && this.cachedDay == currentDay
            && string.Equals(this.cachedLocationName, currentLocationName, StringComparison.OrdinalIgnoreCase))
        {
            travelMinutes = new Dictionary<string, int>(this.cachedReachable, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        // Use SMAPI's non-generic GetApi overload to retrieve the provider's raw API
        // object. GetApi<T> requires T to be a public interface, which is why using
        // GetApi<object> fails. We only need one method, so reflection keeps World
        // Navigator an optional dependency without duplicating its custom route types.
        object? api;
        try
        {
            api = this.helper.ModRegistry.GetApi(ModId);
        }
        catch (Exception ex)
        {
            this.monitor.Log(
                $"World Navigator is loaded, but its SMAPI API could not be retrieved. {ex}",
                LogLevel.Warn
            );
            return false;
        }

        if (api is null)
        {
            this.monitor.Log(
                "World Navigator is loaded, but it did not provide a SMAPI API object. " +
                "Fishing Forecast will use its conservative fallback reachability.",
                LogLevel.Warn
            );
            return false;
        }

        try
        {
            const string methodName = "GetRoutesForCurrentlyReachableLocations";
            MethodInfo? method = FindApiMethod(api, methodName);

            if (method is null)
            {
                Type apiType = api.GetType();
                string interfaces = string.Join(", ", apiType.GetInterfaces().Select(p => p.FullName ?? p.Name));
                string signatures = string.Join("; ", EnumerateApiMethods(apiType)
                    .Where(p => p.Name.Contains("Route", StringComparison.OrdinalIgnoreCase)
                             || p.Name.Contains("Reach", StringComparison.OrdinalIgnoreCase))
                    .Select(FormatMethodSignature)
                    .Distinct());

                this.monitor.Log(
                    $"World Navigator {this.helper.ModRegistry.Get(ModId)?.Manifest.Version} is loaded, but Fishing Forecast couldn't locate {methodName} on its SMAPI API object. " +
                    $"API runtime type: {apiType.FullName}. Interfaces: [{interfaces}]. Route/reachability methods found: [{signatures}].",
                    LogLevel.Warn
                );
                return false;
            }

            if (!this.TryBuildInvocationArguments(method, out object?[]? arguments, out string? unsupportedReason))
            {
                this.monitor.Log(
                    $"World Navigator exposes {FormatMethodSignature(method)}, but Fishing Forecast can't safely supply its required arguments yet. " +
                    $"{unsupportedReason}",
                    LogLevel.Warn
                );
                return false;
            }

            object? result = method.Invoke(api, arguments);
            if (result is null)
            {
                this.monitor.Log("World Navigator returned no reachability data; using fallback reachability.", LogLevel.Warn);
                return false;
            }

            int found = 0;
            foreach ((object? key, object? value) in EnumerateDictionaryLike(result))
            {
                string? locationName = ExtractLocationName(key);
                if (string.IsNullOrWhiteSpace(locationName))
                    continue;

                travelMinutes[locationName] = EstimateTravelMinutes(value);
                found++;
            }

            // The current location is always reachable even if World Navigator
            // omits the zero-length route from its dictionary.
            if (Game1.currentLocation is not null)
                travelMinutes[Game1.currentLocation.NameOrUniqueName] = 0;

            // Immediately after a new day begins, World Navigator may still be
            // rebuilding its daily graph. In that state its API can temporarily
            // report only the current map. Never cache that result: wait briefly
            // and retry while our loading menu is open. If it still isn't ready,
            // fall back for that opening instead of poisoning the day's cache.
            if (travelMinutes.Count <= 1)
            {
                bool sameIncompleteContext =
                    this.incompleteDay == currentDay
                    && string.Equals(this.incompleteLocationName, currentLocationName, StringComparison.OrdinalIgnoreCase);

                if (!sameIncompleteContext)
                {
                    this.incompleteDay = currentDay;
                    this.incompleteLocationName = currentLocationName;
                    this.incompleteAttempts = 0;
                }

                this.incompleteAttempts++;
                if (this.incompleteAttempts <= 2)
                {
                    throw new WorldNavigatorNotReadyException(
                        $"World Navigator returned only the current location on attempt {this.incompleteAttempts}; its daily world scan may still be initializing."
                    );
                }

                // Never promote a one-location warm-up result to an authoritative
                // reachable set. After two short retries, fall back for this opening
                // so the menu still has useful results; the next manual refresh will
                // ask World Navigator again.
                this.monitor.Log(
                    "World Navigator still reports only the current location after two retries; using the conservative fallback for this opening instead of caching an incomplete daily scan.",
                    LogLevel.Warn
                );
                return false;
            }
            else
            {
                this.incompleteDay = -1;
                this.incompleteLocationName = null;
                this.incompleteAttempts = 0;
            }

            if (found == 0)
            {
                this.monitor.Log(
                    $"World Navigator API returned '{result.GetType().FullName}', but Fishing Forecast couldn't read any destination entries from it. " +
                    "Using fallback reachability.",
                    LogLevel.Warn
                );
                return false;
            }

            if (travelMinutes.Count > 1)
            {
                this.cachedLocationName = currentLocationName;
                this.cachedDay = currentDay;
                this.cachedReachable = new Dictionary<string, int>(travelMinutes, StringComparer.OrdinalIgnoreCase);
            }

            this.monitor.Log($"Fishing Forecast received {found} reachable locations from World Navigator using {FormatMethodSignature(method)}.", LogLevel.Trace);
            return true;
        }
        catch (WorldNavigatorNotReadyException)
        {
            throw;
        }
        catch (TargetInvocationException ex)
        {
            Exception actual = ex.InnerException ?? ex;
            this.monitor.Log(
                $"World Navigator threw an error while calculating reachable locations; using fallback reachability. {actual}",
                LogLevel.Warn
            );
            return false;
        }
        catch (Exception ex)
        {
            this.monitor.Log(
                $"Couldn't read World Navigator reachability data; using fallback reachability instead. {ex}",
                LogLevel.Warn
            );
            return false;
        }
    }

    /// <summary>
    /// Find the named API method on the concrete class or any implemented interface.
    /// Parameter count is deliberately NOT restricted: World Navigator may add
    /// optional parameters while preserving source-level no-argument calls.
    /// </summary>
    private static MethodInfo? FindApiMethod(object api, string methodName)
    {
        Type apiType = api.GetType();

        MethodInfo? best = EnumerateApiMethods(apiType)
            .Where(p => string.Equals(p.Name, methodName, StringComparison.Ordinal)
                     || p.Name.EndsWith("." + methodName, StringComparison.Ordinal))
            // Prefer the overload that can be invoked entirely from defaults.
            .OrderBy(p => p.GetParameters().Count(q => !q.IsOptional && !q.HasDefaultValue))
            .ThenBy(p => p.GetParameters().Length)
            .FirstOrDefault();

        return best;
    }

    private static IEnumerable<MethodInfo> EnumerateApiMethods(Type apiType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (MethodInfo method in apiType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            string key = method.Module.ModuleVersionId + ":" + method.MetadataToken;
            if (seen.Add(key))
                yield return method;
        }

        foreach (Type iface in apiType.GetInterfaces())
        {
            foreach (MethodInfo method in iface.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string key = method.Module.ModuleVersionId + ":" + method.MetadataToken;
                if (seen.Add(key))
                    yield return method;
            }
        }
    }

    private bool TryBuildInvocationArguments(MethodInfo method, out object?[] arguments, out string? reason)
    {
        ParameterInfo[] parameters = method.GetParameters();
        arguments = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];

            if (parameter.HasDefaultValue)
            {
                object? value = parameter.DefaultValue;
                arguments[i] = value is DBNull ? Type.Missing : value;
                continue;
            }

            if (parameter.IsOptional)
            {
                arguments[i] = Type.Missing;
                continue;
            }

            // World Navigator 1.4.x requires the requesting mod's manifest so it
            // can evaluate routes in that mod's context. Supply Fishing Forecast's
            // own manifest, not World Navigator's manifest.
            if (typeof(IManifest).IsAssignableFrom(parameter.ParameterType))
            {
                IManifest? manifest = this.helper.ModRegistry.Get(ThisModId)?.Manifest;
                if (manifest is null)
                {
                    reason = $"Couldn't resolve Fishing Forecast's own manifest ('{ThisModId}') to satisfy required IManifest parameter '{parameter.Name}'.";
                    return false;
                }

                arguments[i] = manifest;
                continue;
            }

            // These are unambiguous context parameters if World Navigator chooses
            // to require them in a future API revision.
            if (typeof(GameLocation).IsAssignableFrom(parameter.ParameterType) && Game1.currentLocation is not null)
            {
                arguments[i] = Game1.currentLocation;
                continue;
            }

            if (typeof(Farmer).IsAssignableFrom(parameter.ParameterType) && Game1.player is not null)
            {
                arguments[i] = Game1.player;
                continue;
            }

            reason = $"Required parameter '{parameter.Name}' has type '{parameter.ParameterType.FullName}' and no default value.";
            return false;
        }

        reason = null;
        return true;
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        string parameters = string.Join(", ", method.GetParameters().Select(p =>
        {
            string suffix = p.HasDefaultValue
                ? $" = {FormatDefaultValue(p.DefaultValue)}"
                : p.IsOptional ? " = <optional>" : "";
            return $"{p.ParameterType.Name} {p.Name}{suffix}";
        }));

        return $"{method.ReturnType.Name} {method.Name}({parameters})";
    }

    private static string FormatDefaultValue(object? value)
    {
        if (value is null)
            return "null";
        if (value is string str)
            return $"\\\"{str}\\\"";
        if (value is DBNull)
            return "<missing>";
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    private static IEnumerable<(object? Key, object? Value)> EnumerateDictionaryLike(object dictionaryLike)
    {
        if (dictionaryLike is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
                yield return (entry.Key, entry.Value);
            yield break;
        }

        if (dictionaryLike is not IEnumerable enumerable)
            yield break;

        foreach (object? entry in enumerable)
        {
            if (entry is null)
                continue;

            Type type = entry.GetType();
            PropertyInfo? key = type.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? value = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (key is not null && value is not null)
                yield return (key.GetValue(entry), value.GetValue(entry));
        }
    }

    private static string? ExtractLocationName(object? value)
    {
        if (value is null)
            return null;
        if (value is string str)
            return str;
        if (value is GameLocation location)
            return location.NameOrUniqueName;

        Type type = value.GetType();
        foreach (string propertyName in new[] { "NameOrUniqueName", "LocationName", "Name", "Id" })
        {
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(value) is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        foreach (string propertyName in new[] { "Location", "GameLocation", "Destination", "TargetLocation" })
        {
            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(value) is object nested)
            {
                string? nestedName = ExtractLocationName(nested);
                if (!string.IsNullOrWhiteSpace(nestedName))
                    return nestedName;
            }
        }

        return null;
    }

    private static int EstimateTravelMinutes(object? route)
    {
        if (route is null)
            return 0;

        // Prefer an actual duration exposed by World Navigator when available. A
        // zero-valued duration isn't useful for a non-empty route, though, so let
        // the transition fallback below handle that case instead of displaying
        // every destination as "here".
        if (TryReadMinutes(route, out int directMinutes) && directMinutes > 0)
            return directMinutes;

        if (route is IEnumerable enumerable && route is not string)
        {
            int steps = 0;
            int summedMinutes = 0;
            bool foundPositiveDuration = false;

            foreach (object? step in enumerable)
            {
                if (step is null)
                    continue;

                steps++;
                if (TryReadMinutes(step, out int stepMinutes) && stepMinutes > 0)
                {
                    summedMinutes += stepMinutes;
                    foundPositiveDuration = true;
                }
            }

            if (foundPositiveDuration)
                return summedMinutes;

            // GetRoutesForCurrentlyReachableLocations returns List<TransitionEdge>.
            // An empty list means the current location. Every non-empty list means
            // at least one actual transition is required. Until WN exposes its ETA
            // directly through this API, use 10 in-game minutes per transition.
            // (The old code used steps - 1, which made every one-edge route 0m.)
            return Math.Max(0, steps * 10);
        }

        return 0;
    }

    private static bool TryReadMinutes(object value, out int minutes)
    {
        Type type = value.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        foreach (string memberName in new[]
        {
            "TotalTravelMinutes", "TravelMinutes", "EstimatedTravelMinutes", "EstimatedMinutes", "Minutes",
            "TotalTravelTime", "TravelTime", "EstimatedTravelTime", "TimeCost", "Duration"
        })
        {
            PropertyInfo? property = type.GetProperty(memberName, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                object? raw = null;
                try { raw = property.GetValue(value); } catch { }
                if (TryConvertDuration(raw, out minutes))
                    return true;
            }

            FieldInfo? field = type.GetField(memberName, flags);
            if (field is not null)
            {
                object? raw = null;
                try { raw = field.GetValue(value); } catch { }
                if (TryConvertDuration(raw, out minutes))
                    return true;
            }
        }

        minutes = 0;
        return false;
    }

    private static bool TryConvertDuration(object? raw, out int minutes)
    {
        if (raw is TimeSpan span)
        {
            minutes = Math.Max(0, (int)Math.Round(span.TotalMinutes));
            return true;
        }

        if (raw is not null && TryConvertNumber(raw, out double numeric))
        {
            minutes = Math.Max(0, (int)Math.Round(numeric));
            return true;
        }

        minutes = 0;
        return false;
    }

    private static bool TryConvertNumber(object value, out double number)
    {
        try
        {
            number = Convert.ToDouble(value);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        catch
        {
            number = 0;
            return false;
        }
    }
}
