using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KKAPI.Maker;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KKMakerBridge
{
    public sealed partial class MakerBridge
    {
        private sealed class Setting
        {
            public string Kind, Category;
            public Setting(string kind, string category = null) { Kind = kind; Category = category; }
        }
        private static Dictionary<string, Setting> Settings(string colors, string numbers, string assets)
        {
            var result = new Dictionary<string, Setting>();
            foreach (string key in colors.Split(' ').Where(x => x.Length > 0)) result.Add(key, new Setting("color"));
            foreach (string key in numbers.Split(' ').Where(x => x.Length > 0)) result.Add(key, new Setting("number"));
            foreach (string pair in assets.Split(' ').Where(x => x.Length > 0)) { var x = pair.Split(':'); result.Add(x[0], new Setting("asset", x[1])); }
            return result;
        }
        private static readonly Dictionary<string, Dictionary<string, Setting>> DesignSettings = new Dictionary<string, Dictionary<string, Setting>> {
            {"face", Settings("eyebrowColor hlUpColor hlDownColor whiteBaseColor whiteSubColor eyelineColor moleColor lipLineColor", "detailPower cheekGlossPower pupilWidth pupilHeight pupilX pupilY hlUpY hlDownY eyelineUpWeight lipGlossPower", "headId:bo_head detailId:mt_face_detail eyebrowId:mt_eyebrow noseId:mt_nose hlUpId:mt_eye_hi_up hlDownId:mt_eye_hi_down whiteId:mt_eye_white eyelineUpId:mt_eyeline_up eyelineDownId:mt_eyeline_down moleId:mt_mole lipLineId:mt_lipline")},
            {"body", Settings("skinMainColor skinSubColor sunburnColor nailColor", "detailPower skinGlossPower nailGlossPower", "detailId:mt_body_detail sunburnId:mt_sunburn")},
            {"left_eye", Settings("baseColor subColor", "gradBlend gradOffsetY gradScale", "id:mt_eye gradMaskId:mt_eye_gradation")},
            {"right_eye", Settings("baseColor subColor", "gradBlend gradOffsetY gradScale", "id:mt_eye gradMaskId:mt_eye_gradation")},
            {"makeup", Settings("eyeshadowColor cheekColor lipColor", "", "eyeshadowId:mt_eyeshadow cheekId:mt_cheek lipId:mt_lip")},
            {"identity", new Dictionary<string, Setting> { {"firstname",new Setting("text")}, {"lastname",new Setting("text")}, {"nickname",new Setting("text")} }}
        };

        private static object DesignObject(ChaControl cha, string group)
        { return DesignObject(cha.chaFile,group); }
        private static object DesignObject(ChaFileControl file, string group)
        {
            switch (group) {
                case "face": return file.custom.face;
                case "body": return file.custom.body;
                case "left_eye": return file.custom.face.pupil[0];
                case "right_eye": return file.custom.face.pupil[1];
                case "makeup": return file.custom.face.baseMakeup;
                case "identity": return file.parameter;
                default: throw new Exception("Unknown design group");
            }
        }
        private static JObject DesignValues(ChaControl cha)
        { return DesignValues(cha.chaFile); }
        private static JObject DesignValues(ChaFileControl file)
        {
            var result = new JObject();
            foreach (var group in DesignSettings) {
                var values = new JObject(); var obj = DesignObject(file, group.Key);
                foreach (var item in group.Value) {
                    var value = obj.GetType().GetProperty(item.Key).GetValue(obj, null);
                    values[item.Key] = value is Color ? Rgba((Color)value) : JToken.FromObject(value);
                }
                result[group.Key] = values;
            }
            return result;
        }
        private static JObject DesignSchema()
        {
            var result = new JObject();
            foreach (var group in DesignSettings) {
                var fields = new JObject();
                foreach (var item in group.Value) {
                    var spec = new JObject { {"type", item.Value.Kind} };
                    if (item.Value.Category != null) spec["category"] = item.Value.Category;
                    if (item.Value.Kind == "number" || item.Value.Kind == "color") { spec["min"] = 0; spec["max"] = 1; }
                    fields[item.Key] = spec;
                }
                result[group.Key] = fields;
            }
            return result;
        }
        private IEnumerator EditDesign(ChaControl cha, JObject p, OperationContext context)
        {
            var changes = p["changes"] as JObject;
            if (changes == null || !changes.HasValues) throw new Exception("changes must contain design groups from get_design");
            var apply = new List<Action>();
            foreach (var group in changes.Properties()) {
                Dictionary<string, Setting> specs;
                var fields = group.Value as JObject;
                if (!DesignSettings.TryGetValue(group.Name, out specs) || fields == null || !fields.HasValues) throw new Exception("Unknown or empty design group " + group.Name);
                var obj = DesignObject(cha, group.Name);
                foreach (var field in fields.Properties()) {
                    Setting spec;
                    if (!specs.TryGetValue(field.Name, out spec)) throw new Exception("Unsupported setting " + group.Name + "." + field.Name);
                    object value;
                    if (spec.Kind == "color") value = ParseColor(field.Value, field.Name);
                    else if (spec.Kind == "number") value = Number(field.Value, field.Name, 0, 1);
                    else if (spec.Kind == "asset") {
                        int id = Integer(field.Value, field.Name, 0, int.MaxValue);
                        ValidateAsset((ChaListDefine.CategoryNo)Enum.Parse(typeof(ChaListDefine.CategoryNo), spec.Category), id); value = id;
                    } else {
                        if (field.Value.Type != JTokenType.String || ((string)field.Value).Length > 64 || ((string)field.Value).Any(char.IsControl)) throw new Exception("Name must be text up to 64 characters without control characters");
                        value = (string)field.Value;
                    }
                    var property = obj.GetType().GetProperty(field.Name); var captured = value;
                    apply.Add(() => property.SetValue(obj, captured, null));
                }
            }
            yield return new WaitForEndOfFrame(); context.Backup = Backup(cha);
            foreach (var action in apply) action();
            // Rebuild using the same persistent card data, including extended mod data.
            yield return cha.ReloadAsync(false, false, false, false, false);
            RefreshAppearanceUI(); MakerAPI.GetMakerBase().customCtrl.UpdateCharaNameText();
            for (int i = 0; i < 3; i++) yield return null;
            context.Result = new JObject { {"values", DesignValues(cha)}, {"backup_card", context.Backup} };
        }
        private static JObject Accessories(ChaControl cha)
        {
            var parts = cha.nowCoordinate.accessory.parts;
            var rows = new JArray();
            for (int i = 0; i < Math.Min(20, parts.Length); i++) {
                var p = parts[i]; var moves = new JArray();
                for (int j = 0; j < p.addMove.GetLength(0); j++) moves.Add(new JObject { {"position",Vec(p.addMove[j,0])},{"rotation",Vec(p.addMove[j,1])},{"scale",Vec(p.addMove[j,2])} });
                rows.Add(new JObject { {"slot",i},{"category",Enum.GetName(typeof(ChaListDefine.CategoryNo),p.type)}, {"id",p.id}, {"name",AssetName((ChaListDefine.CategoryNo)p.type,p.id)}, {"parent",p.parentKey},{"colors",new JArray(p.color.Select(Rgba))},{"transforms",moves},{"no_shake",p.noShake} });
            }
            return new JObject { {"items",rows},{"supported_slots",Math.Min(20,parts.Length)}, {"parents",new JArray(Enum.GetNames(typeof(ChaReference.RefObjKey)).Where(x => x.StartsWith("a_n_")))}, {"note","Base-game slots 0..19. MoreAccessories extra slots are not exposed."} };
        }
        private static Vector3 BoundedVec(JToken token, string name, float min, float max)
        {
            var a = token as JArray;
            if (a == null || a.Count != 3) throw new Exception(name + " must have 3 numbers");
            return new Vector3(Number(a[0],name,min,max),Number(a[1],name,min,max),Number(a[2],name,min,max));
        }
        private IEnumerator EditAccessory(ChaControl cha, JObject p, OperationContext context)
        {
            int slot = Integer(p["slot"],"slot",0,Math.Min(19,cha.nowCoordinate.accessory.parts.Length-1));
            var part = cha.nowCoordinate.accessory.parts[slot]; int category = part.type;
            if (p["category"] != null) {
                string key = (string)p["category"];
                if (!Enum.GetNames(typeof(ChaListDefine.CategoryNo)).Contains(key) || !key.StartsWith("ao_")) throw new Exception("Use an ao_ accessory category");
                category = (int)Enum.Parse(typeof(ChaListDefine.CategoryNo),key);
            }
            bool select = p["category"] != null || p["id"] != null;
            if (category != part.type && category != 120 && p["id"] == null) throw new Exception("Changing category requires an id");
            int id = category == 120 ? 0 : p["id"] == null ? part.id : Integer(p["id"],"id",0,int.MaxValue);
            if (category != 120) ValidateAsset((ChaListDefine.CategoryNo)category,id);
            string parent = p["parent"] == null ? null : (string)p["parent"];
            if (parent != null && (!parent.StartsWith("a_n_") || !Enum.IsDefined(typeof(ChaReference.RefObjKey),parent) || cha.GetReferenceInfo((ChaReference.RefObjKey)Enum.Parse(typeof(ChaReference.RefObjKey),parent)) == null)) throw new Exception("Unknown/unavailable parent; use get_accessories parents");
            int transform = p["transform"] == null ? 0 : Integer(p["transform"],"transform",0,part.addMove.GetLength(0)-1);
            Vector3? position = p["position"] == null ? (Vector3?)null : BoundedVec(p["position"],"position",-100,100);
            Vector3? rotation = p["rotation"] == null ? (Vector3?)null : BoundedVec(p["rotation"],"rotation",-360,360);
            Vector3? scale = p["scale"] == null ? (Vector3?)null : BoundedVec(p["scale"],"scale",0.01f,10);
            var colors = new Dictionary<int,Color>();
            if (p["colors"] != null) {
                var obj = p["colors"] as JObject; if (obj == null) throw new Exception("colors must be an object");
                foreach(var c in obj.Properties()) { int n; if (!int.TryParse(c.Name,out n) || n < 0 || n >= part.color.Length) throw new Exception("Invalid color channel"); colors.Add(n,ParseColor(c.Value,c.Name)); }
            }
            if (!select && parent == null && !position.HasValue && !rotation.HasValue && !scale.HasValue && colors.Count == 0) throw new Exception("No accessory changes supplied");
            if (category == 120 && (parent != null || position.HasValue || rotation.HasValue || scale.HasValue || colors.Count > 0)) throw new Exception("Cannot transform/recolor an empty slot");
            yield return new WaitForEndOfFrame(); context.Backup = Backup(cha);
            if (select) yield return cha.ChangeAccessoryAsync(slot,category,id,parent ?? "",true,false);
            part = cha.nowCoordinate.accessory.parts[slot];
            if (part.type != category || (category != 120 && part.id != id)) throw new Exception("Game substituted requested accessory");
            if (parent != null && !cha.ChangeAccessoryParent(slot,parent)) throw new Exception("Game rejected accessory parent");
            if (position.HasValue) part.addMove[transform,0] = position.Value;
            if (rotation.HasValue) part.addMove[transform,1] = rotation.Value;
            if (scale.HasValue) part.addMove[transform,2] = scale.Value;
            foreach(var c in colors) part.color[c.Key] = c.Value;
            if (category != 120) { cha.UpdateAccessoryMoveFromInfo(slot); cha.ChangeAccessoryColor(slot); }
            cha.AssignCoordinate((ChaFileDefine.CoordinateType)cha.chaFile.status.coordinateType);
            RefreshAppearanceUI(); yield return null;
            context.Result = new JObject { {"accessories",Accessories(cha)}, {"backup_card",context.Backup} };
        }
        private static JArray Coordinates(ChaControl cha)
        {
            return new JArray(cha.chaFile.coordinate.Select((x,i) => new JObject { {"index",i},{"type",Enum.GetName(typeof(ChaFileDefine.CoordinateType),i)},{"name",x.coordinateName},{"active",i == cha.chaFile.status.coordinateType} }));
        }
        private IEnumerator SelectCoordinate(ChaControl cha, JObject p, OperationContext context)
        {
            int index = Integer(p["index"],"index",0,cha.chaFile.coordinate.Length-1);
            yield return new WaitForEndOfFrame(); context.Backup = Backup(cha);
            if (!cha.ChangeCoordinateType((ChaFileDefine.CoordinateType)index,true)) throw new Exception("Game rejected coordinate");
            yield return cha.ReloadAsync(false,true,true,true,false);
            RefreshAppearanceUI(); for(int i=0;i<3;i++) yield return null;
            context.Result = new JObject { {"coordinates",Coordinates(cha)},{"appearance",Appearance(cha)},{"backup_card",context.Backup} };
        }
    }
}
