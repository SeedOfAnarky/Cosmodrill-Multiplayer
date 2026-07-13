using System.Text;
using HarmonyLib;

namespace CosmodrillMultiplayer;

/// <summary>
/// Host-authoritative delivered-resource inventory and station purchase handling.
/// </summary>
public sealed partial class MultiplayerMod
{
    private readonly Dictionary<string, long> currencyRequestSequences = new Dictionary<string, long>();
    private float currencySyncTimer;
    private int currencyDeltasSent;
    private int currencyDeltasReceived;
    private int currencySnapshotsSent;
    private int currencySnapshotsReceived;
    private long nextCurrencyRequestSequence;
    private long currencyRevision;
    private long lastCurrencyRevision;
    private bool applyingSharedCurrency;
    private bool stationCurrencyRequestActive;
    private bool stationCurrencyRequestSucceeded;

    private void InstallEconomyPatches()
    {
        try
        {
            var harmony = new global::HarmonyLib.Harmony("cosmodrill.multiplayer.economy");
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(PlayerCurrency), nameof(PlayerCurrency.ChangeCurrency), new[] { typeof(ResourceTypes), typeof(float) }),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforeCurrencyChange)),
                postfix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(AfterCurrencyChange)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(ResourceDeliveryStation), nameof(ResourceDeliveryStation.BuyResourceFromPlayer), new[] { typeof(string) }),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforeStationResourcePurchase)),
                postfix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(AfterStationResourcePurchase)));
            Log("ECONOMY", "Installed shared station-inventory hooks");
        }
        catch (Exception ex) { LogError("ECONOMY", "Could not install shared inventory hooks: " + ex); }
    }

    private static bool BeforeCurrencyChange(ResourceTypes __0, float __1)
    {
        return active == null || active.HandleCurrencyChangeRequest(__0, __1);
    }

    private static void AfterCurrencyChange(ResourceTypes __0, float __1)
    {
        if (active == null || active.applyingSharedCurrency || !active.Connected || !active.sceneReady || !active.isHost) return;
        active.BroadcastCurrencySnapshot("host currency changed");
    }

    private static void BeforeStationResourcePurchase(ResourceDeliveryStation __instance)
    {
        if (active == null) return;
        active.stationCurrencyRequestActive = active.Connected && active.sceneReady && !active.isHost && __instance != null && !__instance.IsDestroyedStation;
        active.stationCurrencyRequestSucceeded = false;
    }

    private static void AfterStationResourcePurchase(ref bool __result)
    {
        if (active == null || !active.stationCurrencyRequestActive) return;
        if (!active.stationCurrencyRequestSucceeded) __result = false;
        active.stationCurrencyRequestActive = false;
        active.stationCurrencyRequestSucceeded = false;
    }

    private bool HandleCurrencyChangeRequest(ResourceTypes resourceType, float amount)
    {
        if (applyingSharedCurrency || !Connected || !sceneReady || isHost) return true;
        if (!Finite(amount) || amount == 0f || Math.Abs(amount) > 100000f || !Enum.IsDefined(typeof(ResourceTypes), resourceType))
        {
            LogWarn("ECONOMY", "Blocked invalid guest currency delta for " + resourceType + ": " + amount);
            return false;
        }
        long sequence = ++nextCurrencyRequestSequence;
        bool sent = replication != null && replication.Send("D|" + localId + "|" + sequence + "|" + (int)resourceType + "|" + F(amount));
        if (stationCurrencyRequestActive) stationCurrencyRequestSucceeded = sent;
        if (sent) currencyDeltasSent++;
        else LogWarn("ECONOMY", "Shared inventory request could not reach the host; local balance was left unchanged");
        // Guests never directly mutate the shared ledger. The host's absolute
        // snapshot is applied to every peer, including the requesting guest.
        return false;
    }

    private void UpdateCurrencySynchronization()
    {
        if (!Connected || !sceneReady || !isHost || PlayerCurrency.Instance == null) return;
        currencySyncTimer += UnityEngine.Time.unscaledDeltaTime;
        if (currencySyncTimer < 2f) return;
        currencySyncTimer = 0f;
        BroadcastCurrencySnapshot("periodic reconciliation");
    }

    private bool TryHandleEconomyReplication(string[] p, string senderId)
    {
        if (p[0] == "D")
        {
            if (!isHost || p.Length != 5) throw new Exception("Invalid currency delta");
            long sequence = long.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
            int typeValue = int.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
            float amount = P(p[4]);
            if (sequence <= 0 || !Enum.IsDefined(typeof(ResourceTypes), typeValue) || !Finite(amount) || amount == 0f || Math.Abs(amount) > 100000f) throw new Exception("Invalid currency delta values");
            long lastSequence;
            if (currencyRequestSequences.TryGetValue(senderId, out lastSequence) && sequence <= lastSequence) throw new Exception("Stale currency delta sequence");
            currencyRequestSequences[senderId] = sequence;
            ApplyAuthoritativeCurrencyDelta(senderId, (ResourceTypes)typeValue, amount);
            return true;
        }
        if (p[0] == "C")
        {
            if (isHost || p.Length != 10) throw new Exception("Invalid currency snapshot");
            long revision = long.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
            if (revision <= lastCurrencyRevision) return true;
            float[] amounts = new float[7];
            for (int i = 0; i < amounts.Length; i++)
            {
                amounts[i] = P(p[i + 3]);
                if (!Finite(amounts[i]) || amounts[i] < 0f || amounts[i] > 100000000f) throw new Exception("Invalid currency snapshot amount");
            }
            ApplyCurrencySnapshot(revision, amounts);
            return true;
        }
        return false;
    }

    private void ApplyAuthoritativeCurrencyDelta(string playerId, ResourceTypes resourceType, float amount)
    {
        if (PlayerCurrency.Instance == null)
        {
            LogWarn("ECONOMY", "Rejected currency delta because the host inventory is not ready");
            return;
        }
        float current = PlayerCurrency.Instance.GetResourceAmount(resourceType);
        if (amount < 0f && current + amount < -0.001f)
        {
            LogWarn("ECONOMY", "Rejected unaffordable " + resourceType + " spend of " + (-amount) + " from " + playerId.Substring(0, 8) + "; available=" + current);
            BroadcastCurrencySnapshot("rejected unaffordable spend");
            return;
        }
        applyingSharedCurrency = true;
        try { PlayerCurrency.Instance.ChangeCurrency(resourceType, amount); }
        finally { applyingSharedCurrency = false; }
        currencyDeltasReceived++;
        Log("ECONOMY", "Accepted " + playerId.Substring(0, 8) + " " + resourceType + " delta " + (amount > 0f ? "+" : "") + F(amount) + "; shared total=" + F(PlayerCurrency.Instance.GetResourceAmount(resourceType)));
        BroadcastCurrencySnapshot("guest currency delta");
    }

    private void BroadcastCurrencySnapshot(string reason)
    {
        if (!isHost || replication == null || PlayerCurrency.Instance == null) return;
        var frame = new StringBuilder("C|").Append(localId).Append('|').Append(++currencyRevision);
        int resourceCount = Enum.GetValues(typeof(ResourceTypes)).Length;
        for (int i = 0; i < resourceCount; i++) frame.Append('|').Append(F(PlayerCurrency.Instance.GetResourceAmount((ResourceTypes)i)));
        if (replication.Send(frame.ToString())) currencySnapshotsSent++;
    }

    private void ApplyCurrencySnapshot(long revision, float[] amounts)
    {
        if (PlayerCurrency.Instance == null || amounts == null || amounts.Length != Enum.GetValues(typeof(ResourceTypes)).Length) return;
        bool firstSnapshot = lastCurrencyRevision == 0;
        applyingSharedCurrency = true;
        try
        {
            foreach (Currency currency in PlayerCurrency.Instance.CurrencyList)
            {
                int index = (int)currency.CurrencyType;
                if (index < 0 || index >= amounts.Length) continue;
                currency.CurrentCurrencyAmount = amounts[index];
                currency.UpdateCurrencyCounter();
            }
            PlayerCurrency.Instance.CurrencyChanged?.Invoke();
            lastCurrencyRevision = revision;
            currencySnapshotsReceived++;
        }
        finally { applyingSharedCurrency = false; }
        if (firstSnapshot) Log("ECONOMY", "Shared station inventory synchronized at revision " + revision + ": " + FormatCurrencyAmounts(amounts));
    }

    private static string FormatCurrencyAmounts(float[] amounts)
    {
        var result = new StringBuilder();
        for (int i = 0; i < amounts.Length; i++)
        {
            if (i > 0) result.Append(", ");
            result.Append((ResourceTypes)i).Append('=').Append(F(amounts[i]));
        }
        return result.ToString();
    }
}
