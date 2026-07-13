using UnityEngine;

namespace CosmodrillMultiplayer;

/// <summary>
/// Edge-of-screen teammate name, direction, and distance markers. A marker is
/// hidden whenever any visible part of the remote ship intersects the viewport.
/// </summary>
public sealed partial class MultiplayerMod
{
    private bool teammateLocatorEnabled = true;

    private void DrawTeammateLocator()
    {
        if (!teammateLocatorEnabled || !Connected || !sceneReady || avatars.Count == 0 || Camera.main == null || PlayerDrill.Instance == null) return;
        Camera camera = Camera.main;
        Plane[] cameraPlanes = GeometryUtility.CalculateFrustumPlanes(camera);
        Vector3 localPosition = PlayerDrill.Instance.PlayerRB == null ? PlayerDrill.Instance.transform.position : PlayerDrill.Instance.PlayerRB.transform.position;
        const float width = 150f, height = 24f, margin = 28f;
        foreach (KeyValuePair<string, GameObject> entry in avatars)
        {
            if (entry.Value == null) continue;
            if (IsAvatarVisibleInViewport(camera, cameraPlanes, entry.Value)) continue;
            Vector3 screen = camera.WorldToScreenPoint(entry.Value.transform.position);
            float guiX = screen.x;
            float guiY = Screen.height - screen.y;
            float clampedX = Mathf.Clamp(guiX, margin + width * 0.5f, Screen.width - margin - width * 0.5f);
            float clampedY = Mathf.Clamp(guiY, margin + height * 0.5f, Screen.height - margin - height * 0.5f);
            string name;
            if (!peerNames.TryGetValue(entry.Key, out name) || string.IsNullOrWhiteSpace(name)) name = entry.Key.Substring(0, Math.Min(8, entry.Key.Length));
            float distance = Vector2.Distance(localPosition, entry.Value.transform.position);
            string direction = DirectionArrow(guiX - Screen.width * 0.5f, Screen.height * 0.5f - guiY) + " ";
            GUI.Box(new Rect(clampedX - width * 0.5f, clampedY - height * 0.5f, width, height), direction + name + "  " + Mathf.RoundToInt(distance) + "m");
        }
    }

    private static bool IsAvatarVisibleInViewport(Camera camera, Plane[] cameraPlanes, GameObject avatar)
    {
        bool foundBounds = false;
        Bounds combinedBounds = default;
        foreach (SpriteRenderer renderer in avatar.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (renderer == null || !renderer.enabled || renderer.color.a <= 0.01f) continue;
            if (!foundBounds) { combinedBounds = renderer.bounds; foundBounds = true; }
            else combinedBounds.Encapsulate(renderer.bounds);
        }
        if (!foundBounds)
        {
            foreach (Renderer renderer in avatar.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!foundBounds) { combinedBounds = renderer.bounds; foundBounds = true; }
                else combinedBounds.Encapsulate(renderer.bounds);
            }
        }
        if (foundBounds) return GeometryUtility.TestPlanesAABB(cameraPlanes, combinedBounds);
        Vector3 viewport = camera.WorldToViewportPoint(avatar.transform.position);
        return viewport.z > 0f && viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
    }

    private static string DirectionArrow(float x, float y)
    {
        float angle = Mathf.Atan2(y, x) * Mathf.Rad2Deg;
        if (angle >= -22.5f && angle < 22.5f) return "→";
        if (angle >= 22.5f && angle < 67.5f) return "↗";
        if (angle >= 67.5f && angle < 112.5f) return "↑";
        if (angle >= 112.5f && angle < 157.5f) return "↖";
        if (angle >= 157.5f || angle < -157.5f) return "←";
        if (angle >= -157.5f && angle < -112.5f) return "↙";
        if (angle >= -112.5f && angle < -67.5f) return "↓";
        return "↘";
    }
}
