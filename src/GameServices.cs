using System;
using Core.Services;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // The game's ServiceLocator searches every object tagged ServiceObject on each call, and some callers run every
    // frame. Services live as long as their scene, so a found one is kept until Unity destroys it. A miss is retried
    // after half a second, so a server-only service on a client does not cost a search every frame.
    static class GameServices
    {
        const float RetryDelay = 0.5f;

        public static T Get<T>() where T : class
        {
            T cached = ServiceCache<T>.Value;
            if (Alive(cached))
                return cached;
            if (Time.unscaledTime < ServiceCache<T>.RetryAt)
                return null;
            try
            {
                cached = ServiceLocator.GetService<T>();
            }
            catch (Exception)
            {
                cached = null;
            }
            ServiceCache<T>.Value = cached;
            ServiceCache<T>.RetryAt = cached == null ? Time.unscaledTime + RetryDelay : 0f;
            return cached;
        }

        // A service, or else any object of that type in the loaded scenes.
        public static T Find<T>() where T : Object
        {
            T cached = FoundCache<T>.Value;
            if (cached != null)
                return cached;
            if (Time.unscaledTime < FoundCache<T>.RetryAt)
                return null;
            T found = Get<T>();
            if (found == null)
            {
                T[] all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                found = all.Length == 0 ? null : all[0];
            }
            FoundCache<T>.Value = found;
            FoundCache<T>.RetryAt = found == null ? Time.unscaledTime + RetryDelay : 0f;
            return found;
        }

        static bool Alive<T>(T value) where T : class
        {
            return value != null && !(value is Object unity && unity == null);
        }

        static class ServiceCache<T> where T : class
        {
            public static T Value;
            public static float RetryAt;
        }

        static class FoundCache<T> where T : Object
        {
            public static T Value;
            public static float RetryAt;
        }
    }
}
