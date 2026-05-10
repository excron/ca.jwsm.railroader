using Helpers.Animation;
using Track;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Drop-in replacement for <see cref="SwitchStand"/> on a node that
    /// represents a multi-state (e.g. true 3-way) switch. Vanilla
    /// SwitchStand reads the binary <see cref="TrackNode.isThrown"/>
    /// and animates the lever between two positions; this driver reads
    /// <see cref="MultiStateSwitch.CurrentRouteIndex"/> and lerps the
    /// lever between N positions across the same animation clip
    /// timeline.
    ///
    /// Mapping: index 0 -> time 0, index N-1 -> time clip.length, with
    /// even spacing between. So a 3-way uses 0, 0.5, 1.0 of clip length;
    /// a 4-way (slip) would use 0, 0.33, 0.67, 1.0.
    ///
    /// Spawning: <see cref="ThreeWaySwitchRenderer"/> instantiates the
    /// vanilla switch stand prefab, destroys its <see cref="SwitchStand"/>
    /// component, and adds this in its place. The Animator + AnimationClip
    /// references are pulled from the vanilla SwitchStand before destroy.
    /// </summary>
    public class MultiStateSwitchStand : MonoBehaviour
    {
        public TrackNode Node;
        public MultiStateSwitch MSS;
        public Animator Animator;
        public AnimationClip AnimationClip;

        // Time (in clip seconds) per second of real time when the lever is moving
        // between positions. Vanilla uses 1.0; we want the same feel.
        public float ThrowSpeed = 1f;

        private bool _initialized;
        private int _wasIndex = int.MinValue;
        private float _currentTime;
        private float _targetTime;
        private PlayableHandle _playable;

        private int RouteCount => MSS != null && MSS.RouteSegmentIds != null ? MSS.RouteSegmentIds.Length : 0;

        // Note: no OnEnable initialization. AddComponent fires OnEnable before
        // the caller can set our public fields, so we'd always see them as null.
        // Lazy-init in Update once they're populated.

        private void OnDisable()
        {
            _playable?.Pause();
        }

        private void OnDestroy()
        {
            _playable?.Dispose();
            _playable = null;
        }

        private void Update()
        {
            if (!_initialized)
            {
                if (Animator == null || AnimationClip == null || MSS == null) return;
                _playable = Animator.PlayableGraphAdapter().AddPlayable(AnimationClip);
                _wasIndex = MSS.CurrentRouteIndex;
                _currentTime = TimeForIndex(_wasIndex);
                _targetTime  = _currentTime;
                _playable.Time = _currentTime;
                _playable.Speed = 0f;
                _playable.Play();
                _initialized = true;
            }

            if (MSS.CurrentRouteIndex != _wasIndex)
            {
                _wasIndex = MSS.CurrentRouteIndex;
                _targetTime = TimeForIndex(_wasIndex);
            }

            if (!Mathf.Approximately(_currentTime, _targetTime))
            {
                float delta = ThrowSpeed * Time.deltaTime;
                _currentTime = Mathf.MoveTowards(_currentTime, _targetTime, delta);
                _playable.Time = _currentTime;
                _playable.ClampTimeToClipBounds();
            }
        }

        private float TimeForIndex(int idx)
        {
            int n = RouteCount;
            if (n <= 1) return 0f;
            // Even spacing across the clip timeline.
            float t = (float)idx / (n - 1);
            return t * AnimationClip.length;
        }
    }
}
