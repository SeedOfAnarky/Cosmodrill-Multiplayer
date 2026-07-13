using UnityEngine;

namespace CosmodrillMultiplayer;

internal sealed class RemotePlayerAvatar : MonoBehaviour
{
    private Vector3 targetPosition;
    private Vector3 targetVelocity;
    private Vector3 smoothVelocity;
    private float targetRotation;
    private float rotationVelocity;
    private float lastStateTime;
    private Animator drillAnimator;
    private Transform visualRoot;
    private SpriteRenderer mainRocket;
    private SpriteRenderer leftRocket;
    private SpriteRenderer rightRocket;
    private ParticleSystem rocketTrail;
    private bool initialized;

    internal void Initialize(Animator animator, Transform visuals, SpriteRenderer main, SpriteRenderer left, SpriteRenderer right, ParticleSystem trail)
    {
        drillAnimator = animator; visualRoot = visuals; mainRocket = main; leftRocket = left; rightRocket = right; rocketTrail = trail; targetPosition = transform.position; targetRotation = transform.eulerAngles.z; initialized = true;
    }

    internal void SetTarget(Vector3 position, float rotation, Vector2 velocity, bool drilling, bool moving, bool leftJet, bool rightJet)
    {
        if (!initialized) { targetPosition = position; targetRotation = rotation; transform.position = position; transform.rotation = Quaternion.Euler(0f, 0f, rotation); initialized = true; }
        targetPosition = position; targetRotation = rotation; targetVelocity = velocity; lastStateTime = Time.unscaledTime;
        if (drillAnimator != null) drillAnimator.SetBool("Drilling", drilling);
        if (mainRocket != null) mainRocket.enabled = moving;
        if (leftRocket != null) leftRocket.enabled = leftJet;
        if (rightRocket != null) rightRocket.enabled = rightJet;
        if (rocketTrail != null)
        {
            if (moving && !rocketTrail.isPlaying) rocketTrail.Play(true);
            else if (!moving && rocketTrail.isPlaying) rocketTrail.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    private void Update()
    {
        float age = Mathf.Clamp(Time.unscaledTime - lastStateTime, 0f, 0.15f);
        Vector3 predicted = targetPosition + targetVelocity * age;
        if ((transform.position - predicted).sqrMagnitude > 16f) { transform.position = predicted; smoothVelocity = Vector3.zero; }
        else transform.position = Vector3.SmoothDamp(transform.position, predicted, ref smoothVelocity, 0.075f, 100f, Time.unscaledDeltaTime);
        float angle = Mathf.SmoothDampAngle(transform.eulerAngles.z, targetRotation, ref rotationVelocity, 0.065f, 720f, Time.unscaledDeltaTime);
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }
}
