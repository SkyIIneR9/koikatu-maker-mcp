using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace KKMakerBridge
{
    public sealed partial class MakerBridge
    {
        private static readonly string[] HairSlots = { "back", "front", "side", "extension" };
        private static readonly ChaListDefine.CategoryNo[] HairCategories = {
            ChaListDefine.CategoryNo.bo_hair_b, ChaListDefine.CategoryNo.bo_hair_f,
            ChaListDefine.CategoryNo.bo_hair_s, ChaListDefine.CategoryNo.bo_hair_o
        };
        private static readonly string[] ClothesSlots = { "top", "bottom", "bra", "shorts", "gloves", "pantyhose", "socks", "inner_shoes", "outer_shoes" };
        private static readonly ChaListDefine.CategoryNo[] ClothesCategories = {
            ChaListDefine.CategoryNo.co_top, ChaListDefine.CategoryNo.co_bot,
            ChaListDefine.CategoryNo.co_bra, ChaListDefine.CategoryNo.co_shorts,
            ChaListDefine.CategoryNo.co_gloves, ChaListDefine.CategoryNo.co_panst,
            ChaListDefine.CategoryNo.co_socks, ChaListDefine.CategoryNo.co_shoes, ChaListDefine.CategoryNo.co_shoes
        };

        private static JArray Rgba(Color color) { return new JArray(color.r, color.g, color.b, color.a); }
        private static Color ParseColor(JToken raw, string name)
        {
            var color = raw as JArray;
            if (color == null || (color.Count != 3 && color.Count != 4)) throw new Exception(name + " must be [r,g,b] or [r,g,b,a], with values 0..1");
            return new Color(Number(color[0], name, 0, 1), Number(color[1], name, 0, 1), Number(color[2], name, 0, 1), color.Count == 4 ? Number(color[3], name, 0, 1) : 1);
        }
        private static int Integer(JToken raw, string name, int min, int max)
        {
            if (raw == null || raw.Type != JTokenType.Integer) throw new Exception(name + " must be an integer");
            long n = (long)raw;
            if (n < min || n > max) throw new Exception(name + " is out of range");
            return (int)n;
        }
        private static int Slot(JToken raw, string[] names)
        {
            if (raw == null || raw.Type != JTokenType.String) throw new Exception("slot must be one of: " + string.Join(", ", names));
            int n = Array.IndexOf(names, ((string)raw).ToLowerInvariant());
            if (n < 0) throw new Exception("Unknown slot. Use: " + string.Join(", ", names));
            return n;
        }
        private static void ValidateAsset(ChaListDefine.CategoryNo category, int id)
        {
            var items = Singleton<Manager.Character>.Instance.chaListCtrl.GetCategoryInfo(category);
            if (items == null || !items.ContainsKey(id)) throw new Exception("Asset ID " + id + " is not installed in " + category + "; use maker_list_assets");
        }
        private static string AssetName(ChaListDefine.CategoryNo category, int id)
        {
            var items = Singleton<Manager.Character>.Instance.chaListCtrl.GetCategoryInfo(category);
            ListInfoBase item;
            return items != null && items.TryGetValue(id, out item) ? item.Name : null;
        }

        private static JArray HairInfo(ChaFileHair hair)
        {
            var rows = new JArray();
            for (int i = 0; i < hair.parts.Length && i < HairSlots.Length; i++)
            {
                var part = hair.parts[i];
                rows.Add(new JObject {
                    {"slot", HairSlots[i]}, {"category", HairCategories[i].ToString()}, {"id", part.id},
                    {"name", AssetName(HairCategories[i], part.id)}, {"base_color", Rgba(part.baseColor)},
                    {"start_color", Rgba(part.startColor)}, {"end_color", Rgba(part.endColor)},
                    {"outline_color", Rgba(part.outlineColor)}, {"length", part.length}
                });
            }
            return rows;
        }
        private static JArray ClothesInfo(ChaFileClothes clothes)
        {
            var rows = new JArray();
            for (int i = 0; i < clothes.parts.Length && i < ClothesSlots.Length; i++)
            {
                var part = clothes.parts[i];
                rows.Add(new JObject {
                    {"slot", ClothesSlots[i]}, {"category", ClothesCategories[i].ToString()}, {"id", part.id},
                    {"name", AssetName(ClothesCategories[i], part.id)},
                    {"colors", new JArray(part.colorInfo.Select(x => Rgba(x.baseColor)))}
                });
            }
            return rows;
        }
        private static JObject Appearance(ChaControl cha)
        {
            return new JObject {
                {"sex", cha.sex == 0 ? "male" : "female"},
                {"coordinate_index", cha.chaFile.status.coordinateType},
                {"coordinate_name", Enum.GetName(typeof(ChaFileDefine.CoordinateType), cha.chaFile.status.coordinateType)},
                {"hair", HairInfo(cha.chaFile.custom.hair)}, {"clothes", ClothesInfo(cha.nowCoordinate.clothes)}
            };
        }

        private string Backup(ChaControl cha)
        {
            var saved = (JObject)SaveCardTo(cha, new JObject { {"name", "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)} }, true);
            return (string)saved["path"];
        }

        private IEnumerator EditHair(ChaControl cha, JObject p, OperationContext context)
        {
            int slot = Slot(p["slot"], HairSlots);
            int? id = p["id"] == null ? (int?)null : Integer(p["id"], "id", 0, int.MaxValue);
            if (id.HasValue) ValidateAsset(HairCategories[slot], id.Value);
            var colors = new Dictionary<string, Color>();
            foreach (string key in new[] { "base_color", "start_color", "end_color", "outline_color" })
                if (p[key] != null) colors.Add(key, ParseColor(p[key], key));
            float? length = p["length"] == null ? (float?)null : Number(p["length"], "length", 0, 1);
            if (!id.HasValue && colors.Count == 0 && !length.HasValue) throw new Exception("Provide a hair ID, color or length");
            yield return new WaitForEndOfFrame();
            context.Backup = Backup(cha);
            if (id.HasValue)
            {
                yield return cha.ChangeHairAsync(slot, id.Value, true, false);
                if (cha.chaFile.custom.hair.parts[slot].id != id.Value) throw new Exception("Game substituted the requested hair asset; restore backup_card if needed");
            }
            var part = cha.chaFile.custom.hair.parts[slot];
            if (colors.ContainsKey("base_color")) part.baseColor = colors["base_color"];
            if (colors.ContainsKey("start_color")) part.startColor = colors["start_color"];
            if (colors.ContainsKey("end_color")) part.endColor = colors["end_color"];
            if (colors.ContainsKey("outline_color")) part.outlineColor = colors["outline_color"];
            if (length.HasValue) part.length = length.Value;
            cha.ChangeSettingHairColor(slot, true, true, true);
            cha.ChangeSettingHairOutlineColor(slot);
            cha.ChangeSettingHairLength(slot);
            RefreshAppearanceUI();
            yield return null;
            context.Result = new JObject { {"appearance", Appearance(cha)}, {"backup_card", context.Backup} };
        }

        private IEnumerator EditClothes(ChaControl cha, JObject p, OperationContext context)
        {
            int slot = Slot(p["slot"], ClothesSlots);
            int? id = p["id"] == null ? (int?)null : Integer(p["id"], "id", 0, int.MaxValue);
            if (id.HasValue) ValidateAsset(ClothesCategories[slot], id.Value);
            var colors = new Dictionary<int, Color>();
            if (p["colors"] != null)
            {
                var obj = p["colors"] as JObject;
                if (obj == null) throw new Exception("colors must be an object keyed by color channel index");
                foreach (var prop in obj.Properties())
                {
                    int channel;
                    if (!int.TryParse(prop.Name, out channel) || channel < 0 || channel >= cha.nowCoordinate.clothes.parts[slot].colorInfo.Length) throw new Exception("Unknown clothes color channel " + prop.Name);
                    if (colors.ContainsKey(channel)) throw new Exception("Duplicate color channel");
                    colors.Add(channel, ParseColor(prop.Value, prop.Name));
                }
            }
            if (!id.HasValue && colors.Count == 0) throw new Exception("Provide a clothes ID or colors");
            yield return new WaitForEndOfFrame();
            context.Backup = Backup(cha);
            if (id.HasValue)
            {
                var sub = cha.nowCoordinate.clothes.subPartsId;
                yield return cha.ChangeClothesAsync(slot, id.Value, sub[0], sub[1], sub[2], true, false);
                if (cha.nowCoordinate.clothes.parts[slot].id != id.Value) throw new Exception("Game substituted the requested clothes asset; restore backup_card if needed");
            }
            var part = cha.nowCoordinate.clothes.parts[slot];
            foreach (var kv in colors) part.colorInfo[kv.Key].baseColor = kv.Value;
            // Some clothes do not expose every color channel; inspect the screenshot.
            cha.ChangeCustomClothes(true, slot, true, true, true, true, true);
            cha.AssignCoordinate((ChaFileDefine.CoordinateType)cha.chaFile.status.coordinateType);
            RefreshAppearanceUI();
            yield return null;
            context.Result = new JObject { {"appearance", Appearance(cha)}, {"backup_card", context.Backup} };
        }

        private static void RefreshAppearanceUI()
        {
            var maker = MakerAPI.GetMakerBase();
            maker.updateCvsHairFront = maker.updateCvsHairBack = maker.updateCvsHairSide = maker.updateCvsHairExtension = maker.updateCvsHairEtc = true;
            maker.updateCvsCosTop = maker.updateCvsCosBot = maker.updateCvsCosBra = maker.updateCvsCosShorts = true;
            maker.updateCvsCosGloves = maker.updateCvsCosPanst = maker.updateCvsCosSocks = maker.updateCvsCosInnerShoes = maker.updateCvsCosOuterShoes = true;
            maker.updateCustomUI = true;
        }

        private string CardPath(string input)
        {
            if (string.IsNullOrEmpty(input)) throw new Exception("path is required");
            string root = Path.GetFullPath(Path.Combine(gameRoot, "UserData/chara"));
            string path = Path.GetFullPath(Path.IsPathRooted(input) ? input : Path.Combine(root, input));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new Exception("Card must be a PNG under UserData/chara");
            RejectLinks(path, root);
            if (!File.Exists(path)) throw new Exception("Card not found");
            return path;
        }

        private static void RejectLinks(string path, string root)
        {
            string current = path;
            while (current != null && current.Length >= root.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new Exception("Linked card directories/files are not supported");
                current = Path.GetDirectoryName(current);
            }
        }

        private static IEnumerable<string> CardFiles(string dir)
        {
            foreach (string file in Directory.GetFiles(dir, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (string child in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (Path.GetFileName(child) != "_backups" && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    foreach (string file in CardFiles(child)) yield return file;
        }

        private JToken ListCards(ChaControl cha, JObject p)
        {
            string sex = (string)p["sex"] ?? (cha.sex == 0 ? "male" : "female");
            if (sex != "male" && sex != "female") throw new Exception("sex must be male or female");
            int offset = p["offset"] == null ? 0 : Integer(p["offset"], "offset", 0, 100000);
            int limit = p["limit"] == null ? 30 : Integer(p["limit"], "limit", 1, 100);
            string search = (string)p["search"] ?? "";
            string root = Path.GetFullPath(Path.Combine(gameRoot, "UserData/chara"));
            string dir = Path.Combine(root, sex);
            RejectLinks(dir, root);
            var found = Directory.Exists(dir) ? CardFiles(dir).Where(x => x.Substring(root.Length).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).Skip(offset).Take(limit + 1).ToList() : new List<string>();
            return new JObject {
                {"has_more", found.Count > limit},
                {"items", new JArray(found.Take(limit).Select(x => new JObject { {"path", x}, {"relative_path", x.Substring(root.Length + 1)}, {"filename", Path.GetFileName(x)} }))}
            };
        }

        private IEnumerator LoadCard(ChaControl cha, JObject p, OperationContext context)
        {
            string path = CardPath((string)p["path"]);
            var probe = new ChaFileControl();
            if (!probe.LoadCharaFile(path, 255, false, true)) throw new Exception("Game could not read this card");
            if (probe.parameter.sex != cha.sex) throw new Exception("Open a Maker session of the same sex as the card before loading it");
            yield return new WaitForEndOfFrame();
            context.Backup = Backup(cha);
            // KKAPI derives partial-load flags from the Maker's standard checkboxes.
            // Make these match the full-load request while the reload hooks run.
            var savedToggles = new Dictionary<Toggle, bool>();
            foreach (var window in Resources.FindObjectsOfTypeAll<ChaCustom.CustomFileWindow>())
                foreach (var toggle in new[] { window.tglChaLoadFace, window.tglChaLoadBody, window.tglChaLoadHair, window.tglChaLoadParam, window.tglChaLoadCoorde })
                    if (toggle != null && !savedToggles.ContainsKey(toggle)) savedToggles.Add(toggle, toggle.isOn);
            try
            {
                foreach (var entry in savedToggles) entry.Key.isOn = true;
                // Koikatu's LoadFileLimited returns false even on success; the game's
                // own Maker ignores it. The independent probe above validates the file.
                cha.chaFile.LoadFileLimited(path, cha.sex, true, true, true, true, true);
                if (!cha.ChangeCoordinateType(true)) throw new Exception("Failed to select loaded coordinate");
                yield return cha.ReloadAsync(false, false, false, false, false);
                MakerAPI.GetMakerBase().updateCustomUI = true;
                MakerAPI.GetMakerBase().customCtrl.UpdateCharaNameText();
                // Let KKAPI's delayed reload hooks and UI subscribers finish their frame work.
                for (int frame = 0; frame < 3; frame++) yield return null;
                undo.Clear();
                context.Result = new JObject {
                    {"loaded_path", path}, {"backup_card", context.Backup},
                    {"character", CharacterInfo(cha)}, {"appearance", Appearance(cha)},
                    {"note", "Same-sex full base-game load. Third-party plugins run their own reload hooks and custom load toggles."}
                };
            }
            finally { foreach (var entry in savedToggles) if (entry.Key != null) entry.Key.isOn = entry.Value; }
        }
    }
}
