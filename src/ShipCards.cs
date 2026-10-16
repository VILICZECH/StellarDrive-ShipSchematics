using System;
using System.Collections.Generic;
using MelonLoader;
using Ships.Interface.Model.State;
using TMPro;
using UI.Common;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ShipSchematics
{
    // One ship in the list. The list keeps its cards while it is open and only moves, hides and relabels them.
    sealed class ShipCard
    {
        public SchematicFile File;
        public ButtonBase Button;
        public RectTransform Rect;
        public TMP_Text Label;
        public Texture2D Picture;
        public bool Removed;

        public bool Alive => !Removed && Button != null;

        public void Refresh(SchematicFile file)
        {
            File = file;
            if (Label != null && Label.text != file.name)
                Label.text = file.name;
        }

        public void SetPicture(Texture2D picture)
        {
            if (Picture != null && Picture != picture)
                Object.Destroy(Picture);
            Picture = picture;
            if (Button != null)
                Button.SetThumbnail(picture);
        }

        public void ReleasePicture()
        {
            if (Picture != null)
                Object.Destroy(Picture);
            Picture = null;
        }

        public void Remove()
        {
            Removed = true;
            ReleasePicture();
            if (Button != null)
                Object.Destroy(Button.gameObject);
        }
    }

    // Draws the missing pictures in the ship list, one ship at a time, while the list is open.
    sealed class ThumbnailBaker
    {
        const int Size = 256;
        const int FrameLimit = 240;

        // A ship is drawn at most once per game session, so a picture that still looks empty is not redrawn on every
        // opening of the list.
        static readonly HashSet<string> Drawn = new HashSet<string>();

        readonly Queue<ShipCard> _queue = new Queue<ShipCard>();
        SchematicPreview _preview;
        ShipCard _card;
        int _frames;

        public void Add(ShipCard card)
        {
            if (card != null && card.File != null && !Drawn.Contains(card.File.id))
                _queue.Enqueue(card);
        }

        public void Clear()
        {
            _queue.Clear();
            Stop();
        }

        public void Tick()
        {
            if (_preview == null)
            {
                Next();
                return;
            }
            _frames++;
            _preview.Tick();
            if (!_preview.HasFrame)
            {
                if (_frames > FrameLimit)
                    Stop();
                return;
            }
            if (_card.Alive)
            {
                byte[] png = _preview.CapturePng();
                if (png != null)
                {
                    SchematicLibrary.SavePreview(_card.File, png);
                    _card.SetPicture(SchematicLibrary.DecodePng(png));
                }
            }
            Stop();
        }

        void Next()
        {
            while (_queue.Count > 0)
            {
                ShipCard card = _queue.Dequeue();
                if (!card.Alive || !Drawn.Add(card.File.id))
                    continue;
                IShipState ship;
                try
                {
                    ship = SchematicLibrary.Restore(card.File);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Could not read " + card.File.name + " to draw its picture. " + ex.Message);
                    continue;
                }
                var preview = new SchematicPreview(Size);
                if (!preview.Show(ship, card.File))
                    continue;
                _preview = preview;
                _card = card;
                _frames = 0;
                return;
            }
        }

        void Stop()
        {
            if (_preview != null)
                _preview.Hide();
            _preview = null;
            _card = null;
            _frames = 0;
        }
    }

    // Catches the mouse wheel and drags between the cards without drawing anything.
    sealed class ScrollArea : Graphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
        }
    }
}
