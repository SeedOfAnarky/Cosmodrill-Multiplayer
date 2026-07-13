using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace CosmodrillMultiplayer;

/// <summary>
/// Host-authoritative enemy death replication. Kept separate from world, economy,
/// transport, and player-avatar synchronization so combat changes stay isolated.
/// </summary>
public sealed partial class MultiplayerMod
{
    private readonly Dictionary<string, long> enemyKillRequestSequences = new Dictionary<string, long>();
    private readonly Dictionary<string, PendingEnemyDeath> pendingEnemyDeaths = new Dictionary<string, PendingEnemyDeath>();
    private readonly HashSet<int> requestedEnemyDeaths = new HashSet<int>();
    private readonly HashSet<int> appliedEnemyDeaths = new HashSet<int>();
    private readonly Dictionary<string, float> recentlyAppliedEnemyLocators = new Dictionary<string, float>();
    private float pendingEnemyTimer;
    private int enemyKillsRequested;
    private int enemyKillsConfirmed;
    private int enemyKillsApplied;
    private long nextEnemyKillSequence;
    private long enemyKillRevision;
    private bool applyingEnemyDeath;

    private sealed class EnemyLocator
    {
        internal string Kind;
        internal string Mode;
        internal string StableKey;
        internal int GroupIndex;
        internal int SlotIndex;
        internal string ObjectName;
        internal Vector2 Position;
    }

    private sealed class PendingEnemyDeath
    {
        internal EnemyLocator Locator;
        internal float ExpiresAt;
        internal bool AllowDrops;
    }

    private void InstallEnemyPatches()
    {
        try
        {
            var harmony = new global::HarmonyLib.Harmony("cosmodrill.multiplayer.enemies");
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(EnemyHealth), nameof(EnemyHealth.Die), new[] { typeof(bool) }),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforeEnemyHealthDeath)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(PirateStationGunHealth), nameof(PirateStationGunHealth.Die), Type.EmptyTypes),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforePirateGunDeath)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(WormBossHealth), nameof(WormBossHealth.Die), Type.EmptyTypes),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforeWormBossDeath)));
            Log("ENEMY", "Installed shared death hooks for normal enemies, pirate-station guns, and worm bosses");
        }
        catch (Exception ex) { LogError("ENEMY", "Could not install enemy synchronization hooks: " + ex); }
    }

    private static bool BeforeEnemyHealthDeath(EnemyHealth __instance, bool __0)
    {
        return active == null || active.HandleLocalEnemyDeath(__instance, "E", __0);
    }

    private static bool BeforePirateGunDeath(PirateStationGunHealth __instance)
    {
        return active == null || active.HandleLocalEnemyDeath(__instance, "G", false);
    }

    private static bool BeforeWormBossDeath(WormBossHealth __instance)
    {
        return active == null || active.HandleLocalEnemyDeath(__instance, "B", false);
    }

    private bool HandleLocalEnemyDeath(Component target, string kind, bool isSilent)
    {
        if (target == null || (kind == "E" && isSilent)) return true;
        int instanceId = target.GetInstanceID();
        if (applyingEnemyDeath)
        {
            appliedEnemyDeaths.Add(instanceId);
            EnemyLocator nestedLocator = BuildEnemyLocator(target, kind);
            if (nestedLocator != null) recentlyAppliedEnemyLocators[EnemyLocatorIdentity(nestedLocator)] = Time.unscaledTime + 2f;
            return true;
        }
        if (!Connected || !sceneReady || replication == null) return true;
        EnemyLocator locator = BuildEnemyLocator(target, kind);
        if (locator == null)
        {
            LogWarn("ENEMY", "Could not identify dying " + target.gameObject.name + "; allowing local death only");
            return true;
        }
        if (isHost)
        {
            if (!appliedEnemyDeaths.Add(instanceId)) return false;
            recentlyAppliedEnemyLocators[EnemyLocatorIdentity(locator)] = Time.unscaledTime + 2f;
            BroadcastEnemyDeath(locator);
            Log("ENEMY", "Host confirmed local " + EnemyKindName(kind) + " death: " + locator.ObjectName);
            return true;
        }
        if (!requestedEnemyDeaths.Add(instanceId)) return false;
        long sequence = ++nextEnemyKillSequence;
        bool sent = replication.Send(BuildEnemyFrame("K", localId, sequence, locator));
        if (sent)
        {
            enemyKillsRequested++;
            Log("ENEMY", "Requested host confirmation for " + EnemyKindName(kind) + " death: " + locator.ObjectName);
            // Leave the zero-health object in place for the brief round trip. It is
            // destroyed only after the host confirms the authoritative target.
            return false;
        }
        requestedEnemyDeaths.Remove(instanceId);
        LogWarn("ENEMY", "Could not send enemy death to host; allowing local death");
        return true;
    }

    private EnemyLocator BuildEnemyLocator(Component target, string kind)
    {
        if (target == null || target.gameObject == null) return null;
        var locator = new EnemyLocator
        {
            Kind = kind,
            Mode = "H",
            StableKey = BuildStableTransformKey(target.transform),
            GroupIndex = -1,
            SlotIndex = -1,
            ObjectName = target.gameObject.name,
            Position = target.transform.position
        };
        EnemyHealth enemy = target as EnemyHealth;
        if (enemy == null) return locator;
        foreach (EnemySpawner spawner in UnityEngine.Object.FindObjectsByType<EnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (spawner == null || spawner.EnemiesToSpawn == null) continue;
            for (int groupIndex = 0; groupIndex < spawner.EnemiesToSpawn.Count; groupIndex++)
            {
                EnemyGroup group = spawner.EnemiesToSpawn[groupIndex];
                if (group == null || group.spawnedEnemies == null) continue;
                int slot = group.spawnedEnemies.IndexOf(enemy);
                if (slot < 0) continue;
                locator.Mode = "S";
                locator.StableKey = BuildStableTransformKey(spawner.transform);
                locator.GroupIndex = groupIndex;
                locator.SlotIndex = slot;
                return locator;
            }
        }
        return locator;
    }

    private static string BuildStableTransformKey(Transform transform)
    {
        if (transform == null) return "";
        var parts = new List<string>();
        Transform current = transform;
        while (current != null)
        {
            parts.Add(current.gameObject.name + "#" + current.GetSiblingIndex());
            current = current.parent;
        }
        parts.Reverse();
        return transform.gameObject.scene.name + ":" + string.Join("/", parts);
    }

    private static string BuildEnemyFrame(string frameType, string senderId, long sequence, EnemyLocator locator)
    {
        return frameType + "|" + senderId + "|" + sequence + "|" + locator.Kind + "|" + locator.Mode + "|" + B64(locator.StableKey) + "|" + locator.GroupIndex + "|" + locator.SlotIndex + "|" + B64(locator.ObjectName) + "|" + F(locator.Position.x) + "|" + F(locator.Position.y);
    }

    private static string EnemyKindName(string kind)
    {
        if (kind == "G") return "pirate-station gun";
        if (kind == "B") return "worm boss";
        return "enemy";
    }

    private static EnemyLocator ParseEnemyLocator(string[] p)
    {
        if (p == null || p.Length != 11) throw new Exception("Invalid enemy death frame");
        string kind = p[3], mode = p[4];
        if (kind != "E" && kind != "G" && kind != "B") throw new Exception("Invalid enemy kind");
        if (mode != "S" && mode != "H") throw new Exception("Invalid enemy locator mode");
        string stableKey = UB64(p[5]), objectName = UB64(p[8]);
        int groupIndex = int.Parse(p[6], CultureInfo.InvariantCulture), slotIndex = int.Parse(p[7], CultureInfo.InvariantCulture);
        float x = P(p[9]), y = P(p[10]);
        if (stableKey.Length == 0 || stableKey.Length > 768 || objectName.Length == 0 || objectName.Length > 128 || !Finite(x) || !Finite(y)) throw new Exception("Invalid enemy locator values");
        if (groupIndex < -1 || groupIndex > 4096 || slotIndex < -1 || slotIndex > 4096 || (mode == "S" && (groupIndex < 0 || slotIndex < 0))) throw new Exception("Invalid enemy spawner slot");
        return new EnemyLocator { Kind = kind, Mode = mode, StableKey = stableKey, GroupIndex = groupIndex, SlotIndex = slotIndex, ObjectName = objectName, Position = new Vector2(x, y) };
    }

    private bool TryHandleEnemyReplication(string[] p, string senderId)
    {
        if (p[0] == "K")
        {
            if (!isHost || p.Length != 11) throw new Exception("Invalid enemy kill request");
            long sequence = long.Parse(p[2], CultureInfo.InvariantCulture);
            long lastSequence;
            if (sequence <= 0 || (enemyKillRequestSequences.TryGetValue(senderId, out lastSequence) && sequence <= lastSequence)) throw new Exception("Stale enemy kill sequence");
            enemyKillRequestSequences[senderId] = sequence;
            EnemyLocator locator = ParseEnemyLocator(p);
            bool applied = ApplyConfirmedEnemyDeath(locator, true, true);
            BroadcastEnemyDeath(locator);
            Log("ENEMY", "Host confirmed " + senderId.Substring(0, 8) + " " + EnemyKindName(locator.Kind) + " death: " + locator.ObjectName + (applied ? "" : " (deferred until entity is loaded)"));
            return true;
        }
        if (p[0] == "X")
        {
            if (isHost || p.Length != 11) throw new Exception("Invalid enemy death confirmation");
            EnemyLocator locator = ParseEnemyLocator(p);
            bool applied = ApplyConfirmedEnemyDeath(locator, false, true);
            Log("ENEMY", "Applied host-confirmed " + EnemyKindName(locator.Kind) + " death: " + locator.ObjectName + (applied ? "" : " (deferred until entity is loaded)"));
            return true;
        }
        return false;
    }

    private void BroadcastEnemyDeath(EnemyLocator locator)
    {
        if (!isHost || replication == null || locator == null) return;
        enemyKillsConfirmed++;
        replication.Send(BuildEnemyFrame("X", localId, ++enemyKillRevision, locator));
    }

    private bool ApplyConfirmedEnemyDeath(EnemyLocator locator, bool allowDrops, bool queueIfMissing)
    {
        string locatorIdentity = EnemyLocatorIdentity(locator);
        float recentlyAppliedUntil;
        if (recentlyAppliedEnemyLocators.TryGetValue(locatorIdentity, out recentlyAppliedUntil))
        {
            if (Time.unscaledTime <= recentlyAppliedUntil) return true;
            recentlyAppliedEnemyLocators.Remove(locatorIdentity);
        }
        Component target = ResolveEnemyTarget(locator);
        if (target == null)
        {
            if (queueIfMissing)
            {
                pendingEnemyDeaths[locatorIdentity] = new PendingEnemyDeath { Locator = locator, AllowDrops = allowDrops, ExpiresAt = Time.unscaledTime + 12f };
            }
            return false;
        }
        int instanceId = target.GetInstanceID();
        if (!appliedEnemyDeaths.Add(instanceId)) return true;
        recentlyAppliedEnemyLocators[locatorIdentity] = Time.unscaledTime + 2f;
        requestedEnemyDeaths.Remove(instanceId);
        applyingEnemyDeath = true;
        try
        {
            if (locator.Kind == "E")
            {
                EnemyHealth enemy = target as EnemyHealth;
                if (enemy == null) throw new Exception("Resolved enemy has the wrong health type");
                int drops = enemy.amountOfEPToDrop;
                if (!allowDrops) enemy.amountOfEPToDrop = 0;
                try { enemy.Die(false); }
                finally { if (enemy != null) enemy.amountOfEPToDrop = drops; }
            }
            else if (locator.Kind == "G")
            {
                PirateStationGunHealth gun = target as PirateStationGunHealth;
                if (gun == null) throw new Exception("Resolved pirate gun has the wrong health type");
                gun.Die();
            }
            else
            {
                WormBossHealth boss = target as WormBossHealth;
                if (boss == null) throw new Exception("Resolved boss has the wrong health type");
                int drops = boss.amountOfEPToDrop;
                if (!allowDrops) boss.amountOfEPToDrop = 0;
                try { boss.Die(); }
                finally { if (boss != null) boss.amountOfEPToDrop = drops; }
            }
            enemyKillsApplied++;
            pendingEnemyDeaths.Remove(locatorIdentity);
            return true;
        }
        catch
        {
            appliedEnemyDeaths.Remove(instanceId);
            recentlyAppliedEnemyLocators.Remove(locatorIdentity);
            throw;
        }
        finally { applyingEnemyDeath = false; }
    }

    private Component ResolveEnemyTarget(EnemyLocator locator)
    {
        if (locator == null) return null;
        if (locator.Kind == "E")
        {
            if (locator.Mode == "S")
            {
                foreach (EnemySpawner spawner in UnityEngine.Object.FindObjectsByType<EnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (spawner == null || BuildStableTransformKey(spawner.transform) != locator.StableKey || spawner.EnemiesToSpawn == null || locator.GroupIndex >= spawner.EnemiesToSpawn.Count) continue;
                    EnemyGroup group = spawner.EnemiesToSpawn[locator.GroupIndex];
                    if (group == null || group.spawnedEnemies == null || locator.SlotIndex >= group.spawnedEnemies.Count) continue;
                    EnemyHealth exact = group.spawnedEnemies[locator.SlotIndex];
                    if (IsEnemyCandidate(exact, locator)) return exact;
                }
            }
            EnemyHealth best = null; float bestDistance = float.MaxValue;
            foreach (EnemyHealth candidate in UnityEngine.Object.FindObjectsByType<EnemyHealth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsEnemyCandidate(candidate, locator)) continue;
                if (locator.Mode == "H" && BuildStableTransformKey(candidate.transform) == locator.StableKey) return candidate;
                float distance = ((Vector2)candidate.transform.position - locator.Position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            return bestDistance <= 10000f ? best : null;
        }
        if (locator.Kind == "G")
        {
            PirateStationGunHealth best = null; float bestDistance = float.MaxValue;
            foreach (PirateStationGunHealth candidate in UnityEngine.Object.FindObjectsByType<PirateStationGunHealth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsEnemyCandidate(candidate, locator)) continue;
                if (BuildStableTransformKey(candidate.transform) == locator.StableKey) return candidate;
                float distance = ((Vector2)candidate.transform.position - locator.Position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            return bestDistance <= 10000f ? best : null;
        }
        WormBossHealth nearest = null; float nearestDistance = float.MaxValue;
        foreach (WormBossHealth candidate in UnityEngine.Object.FindObjectsByType<WormBossHealth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!IsEnemyCandidate(candidate, locator)) continue;
            if (BuildStableTransformKey(candidate.transform) == locator.StableKey) return candidate;
            float distance = ((Vector2)candidate.transform.position - locator.Position).sqrMagnitude;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = candidate; }
        }
        return nearest;
    }

    private bool IsEnemyCandidate(Component candidate, EnemyLocator locator)
    {
        return candidate != null && candidate.gameObject != null && !appliedEnemyDeaths.Contains(candidate.GetInstanceID()) && candidate.gameObject.name == locator.ObjectName;
    }

    private static string EnemyLocatorIdentity(EnemyLocator locator)
    {
        return locator.Kind + "|" + locator.Mode + "|" + locator.StableKey + "|" + locator.GroupIndex + "|" + locator.SlotIndex + "|" + locator.ObjectName;
    }

    private void ProcessPendingEnemyDeaths()
    {
        if (!sceneReady || (pendingEnemyTimer += Time.unscaledDeltaTime) < 0.5f) return;
        pendingEnemyTimer = 0f;
        foreach (KeyValuePair<string, float> recent in new List<KeyValuePair<string, float>>(recentlyAppliedEnemyLocators))
            if (Time.unscaledTime > recent.Value) recentlyAppliedEnemyLocators.Remove(recent.Key);
        if (pendingEnemyDeaths.Count == 0) return;
        foreach (KeyValuePair<string, PendingEnemyDeath> entry in new List<KeyValuePair<string, PendingEnemyDeath>>(pendingEnemyDeaths))
        {
            if (Time.unscaledTime > entry.Value.ExpiresAt)
            {
                pendingEnemyDeaths.Remove(entry.Key);
                LogWarn("ENEMY", "Expired deferred " + EnemyKindName(entry.Value.Locator.Kind) + " death: " + entry.Value.Locator.ObjectName);
                continue;
            }
            if (ApplyConfirmedEnemyDeath(entry.Value.Locator, entry.Value.AllowDrops, false))
                Log("ENEMY", "Applied deferred " + EnemyKindName(entry.Value.Locator.Kind) + " death: " + entry.Value.Locator.ObjectName);
        }
    }
}
