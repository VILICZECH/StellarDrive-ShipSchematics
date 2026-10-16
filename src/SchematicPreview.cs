using System;
using System.Reflection;
using MelonLoader;
using Ships.Interface.Model.State;
using Ships.Visuals;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // Draws a ship hologram into a square texture, for the save window and the pictures in the ship list.
    public sealed class SchematicPreview
    {
        const int Layer = 0;
        // Far below the world, so the preview camera sees nothing but the hologram.
        static readonly Vector3 Origin = new Vector3(0f, -500000f, 0f);

        readonly int _size;
        GameObject _pivot;
        Camera _camera;
        RenderTexture _texture;
        float _yaw = 25f;
        float _pitch = 18f;
        float _distance = 12f;
        int _refits;
        int _liveFrames;

        public SchematicPreview(int size)
        {
            _size = Mathf.Clamp(size, 64, 2048);
        }

        public RenderTexture Texture => _texture;
        public bool HasFrame { get; private set; }

        public bool Show(IShipState ship, SchematicFile extras)
        {
            Hide();
            if (ship == null)
                return false;

            try
            {
                _pivot = new GameObject("Schematic preview");
                _pivot.transform.position = Origin;
                var lightObject = new GameObject("Schematic preview light");
                lightObject.transform.SetParent(_pivot.transform, false);
                lightObject.transform.localPosition = new Vector3(0f, 4f, -6f);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 80f;
                light.intensity = 12f;
                light.shadows = LightShadows.None;
                ShipPartVisuals visuals = SchematicPlacer.CreateHologram(ship);
                if (visuals == null)
                {
                    Hide();
                    return false;
                }

                visuals.transform.SetParent(_pivot.transform, false);
                Subships.Dress(visuals, ship, extras);
                SetLayer(_pivot, Layer);
                FrameCamera(visuals);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not draw the ship preview" + (extras != null ? " of " + extras.name : "") + ". " + ex.Message);
                Hide();
                return false;
            }
        }

        public void Tick()
        {
            if (_pivot != null && _refits < 8)
            {
                _refits++;
                ShipPartVisuals visuals = _pivot.GetComponentInChildren<ShipPartVisuals>(true);
                if (visuals != null)
                {
                    SetLayer(visuals.gameObject, Layer);
                    FrameCamera(visuals);
                }
            }
            if (_camera == null || !_camera.enabled || _refits < 8)
                return;
            _liveFrames--;
            if (_liveFrames > 0)
                return;
            _camera.enabled = false;
            HasFrame = true;
        }

        public void Rotate(float yawDelta, float pitchDelta)
        {
            if (_pivot == null)
                return;
            _yaw += yawDelta;
            _pitch = Mathf.Clamp(_pitch + pitchDelta, -20f, 70f);
            ApplyCamera();
        }

        public byte[] CapturePng()
        {
            if (_texture == null)
                return null;
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(_texture.width, _texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = _texture;
                image.ReadPixels(new Rect(0f, 0f, _texture.width, _texture.height), 0, 0);
                return image.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.Destroy(image);
            }
        }

        public void Hide()
        {
            if (_camera != null)
                Object.Destroy(_camera.gameObject);
            if (_texture != null)
            {
                _texture.Release();
                Object.Destroy(_texture);
            }
            if (_pivot != null)
                Object.Destroy(_pivot);
            _camera = null;
            _texture = null;
            _pivot = null;
            _refits = 0;
            _liveFrames = 0;
            HasFrame = false;
        }

        void FrameCamera(ShipPartVisuals visuals)
        {
            Bounds bounds = new Bounds(visuals.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer renderer in visuals.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null)
                    continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            if (any)
            {
                _pivot.transform.position = Origin;
                visuals.transform.position -= bounds.center - _pivot.transform.position;
                _distance = Mathf.Max(bounds.extents.magnitude * 2.4f, 6f);
            }
            if (_camera == null)
                CreateCamera();
            else
                ApplyCamera();
        }

        void CreateCamera()
        {
            var descriptor = new RenderTextureDescriptor(_size, _size, RenderTextureFormat.ARGB32, 24);
            descriptor.msaaSamples = 1;
            descriptor.useMipMap = false;
            _texture = new RenderTexture(descriptor);
            _texture.Create();
            var cameraObject = new GameObject("Schematic preview camera");
            _camera = cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            Camera main = Camera.main;
            if (main != null)
                CopyUrpData(main, _camera);
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.forceIntoRenderTexture = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.04f, 0.08f, 0.12f, 1f);
            _camera.fieldOfView = 35f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = Mathf.Max(40f, _distance * 4f);
            _camera.rect = new Rect(0f, 0f, 1f, 1f);
            _camera.aspect = 1f;
            _camera.targetTexture = _texture;
            _camera.cullingMask = 1 << Layer;
            _camera.depth = -50f;
            ApplyCamera();
        }

        // Camera.Render() draws no hologram in this game's render pipeline, so the camera is switched on for two
        // frames instead. Leaving it on would render a second full frame every frame.
        void ApplyCamera()
        {
            if (_camera == null || _pivot == null)
                return;
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            _camera.transform.position = _pivot.transform.position + rotation * new Vector3(0f, 0f, -_distance);
            _camera.farClipPlane = Mathf.Max(40f, _distance * 4f);
            _camera.cullingMask = 1 << Layer;
            _camera.targetTexture = _texture;
            _camera.transform.LookAt(_pivot.transform.position);
            _liveFrames = 2;
            _camera.enabled = true;
        }

        static void CopyUrpData(Camera source, Camera destination)
        {
            Component data = null;
            foreach (Component component in source.GetComponents<Component>())
            {
                if (component != null && component.GetType().Name == "UniversalAdditionalCameraData")
                {
                    data = component;
                    break;
                }
            }
            if (data == null)
                return;

            try
            {
                Component copy = destination.gameObject.AddComponent(data.GetType());
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo renderer = data.GetType().GetField("m_RendererIndex", flags);
                if (renderer != null)
                    renderer.SetValue(copy, renderer.GetValue(data));
                PropertyInfo renderType = data.GetType().GetProperty("renderType", flags);
                if (renderType != null && renderType.CanWrite)
                    renderType.SetValue(copy, Enum.ToObject(renderType.PropertyType, 0), null);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not copy the camera settings for the ship preview. " + ex.Message);
            }
        }

        static void SetLayer(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayer(child.gameObject, layer);
        }
    }
}
