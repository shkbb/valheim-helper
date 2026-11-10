using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ValheimHelper
{
    // The game's own icons of every item, building piece and status effect as PNG files in
    // ValheimHelper/wiki/icons/{items,pieces,effects}/<prefab>.png, for the pictures of wiki articles the English wiki
    // has none for yet. The icons live in texture atlases the game cannot read back, so each atlas is copied once
    // through a render texture and every icon is cut out of that copy.
    internal static class GameIconExport
    {
        internal static string Folder => Path.Combine(Path.Combine(Loader.Dir ?? ".", "wiki"), "icons");

        private static readonly Dictionary<Texture, Texture2D> s_readable = new Dictionary<Texture, Texture2D>();

        internal static int Export(ObjectDB db)
        {
            int written = 0;
            try
            {
                HashSet<string> pieces = new HashSet<string>();
                foreach (GameObject prefab in db.m_items)
                {
                    ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                    ItemDrop.ItemData.SharedData shared = drop?.m_itemData?.m_shared;
                    if (shared == null)
                    {
                        continue;
                    }
                    if (shared.m_icons != null && shared.m_icons.Length > 0)
                    {
                        written += Save(shared.m_icons[0], "items", prefab.name);
                    }
                    if (shared.m_buildPieces?.m_pieces == null)
                    {
                        continue;
                    }
                    foreach (GameObject piecePrefab in shared.m_buildPieces.m_pieces)
                    {
                        Piece piece = piecePrefab != null ? piecePrefab.GetComponent<Piece>() : null;
                        if (piece != null && pieces.Add(piecePrefab.name))
                        {
                            written += Save(piece.m_icon, "pieces", piecePrefab.name);
                        }
                    }
                }
                foreach (StatusEffect effect in db.m_StatusEffects)
                {
                    if (effect != null)
                    {
                        written += Save(effect.m_icon, "effects", effect.name);
                    }
                }
            }
            finally
            {
                foreach (Texture2D copy in s_readable.Values)
                {
                    UnityEngine.Object.Destroy(copy);
                }
                s_readable.Clear();
            }
            Log.Info($"Game icons exported: {written}");
            return written;
        }

        private static int Save(Sprite sprite, string kind, string name)
        {
            if (sprite == null || sprite.texture == null || string.IsNullOrEmpty(name))
            {
                return 0;
            }
            try
            {
                Texture2D atlas = Readable(sprite.texture);
                Rect rect = sprite.textureRect;
                int x = Mathf.RoundToInt(rect.x);
                int y = Mathf.RoundToInt(rect.y);
                int width = Mathf.RoundToInt(rect.width);
                int height = Mathf.RoundToInt(rect.height);
                if (width <= 0 || height <= 0 || x + width > atlas.width || y + height > atlas.height)
                {
                    return 0;
                }
                Texture2D icon = new Texture2D(width, height, TextureFormat.RGBA32, false);
                icon.SetPixels(atlas.GetPixels(x, y, width, height));
                icon.Apply();
                string dir = Path.Combine(Folder, kind);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, Safe(name) + ".png"), icon.EncodeToPNG());
                UnityEngine.Object.Destroy(icon);
                return 1;
            }
            catch (Exception e)
            {
                Log.Warn($"Icon {kind}/{name}: {e.Message}");
                return 0;
            }
        }

        // A copy of a texture that can be read: drawn into a render texture without colour conversion, read back.
        private static Texture2D Readable(Texture2D texture)
        {
            if (s_readable.TryGetValue(texture, out Texture2D copy))
            {
                return copy;
            }
            RenderTexture target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
            s_readable[texture] = copy;
            return copy;
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
