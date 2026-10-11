using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KKMakerBridge
{
    public sealed partial class MakerBridge
    {
        private const string BoneType = "KKABMX.Core.BoneController";
        private const string MaterialType = "KK_Plugins.MaterialEditor.MaterialEditorCharaController";
        private const string ResolverType = "Sideloader.AutoResolver.UniversalAutoResolver";
        private static Type OptionalType(string name) { return AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name,false)).FirstOrDefault(t=>t != null); }
        private static object ReadMember(object obj,string name)
        {
            var t=obj.GetType(); var p=t.GetProperty(name); if(p != null) return p.GetValue(obj,null);
            var f=t.GetField(name); if(f != null) return f.GetValue(obj);
            throw new Exception("Installed plugin API lacks " + name);
        }
        private static void WriteMember(object obj,string name,object value)
        {
            var t=obj.GetType(); var p=t.GetProperty(name); if(p != null) { p.SetValue(obj,value,null); return; }
            var f=t.GetField(name); if(f != null) { f.SetValue(obj,value); return; }
            throw new Exception("Installed plugin API lacks " + name);
        }
        private static object Invoke(object obj,string name,params object[] args)
        {
            Type type=obj as Type ?? obj.GetType();
            var methods=type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static).Where(m=>m.Name == name && m.GetParameters().Length == args.Length && m.GetParameters().Select((p,i)=>args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x=>x)).ToArray();
            if(methods.Length != 1) throw new Exception("Unsupported or ambiguous plugin API: " + name);
            try { return methods[0].Invoke(obj is Type ? null : obj,args); }
            catch(TargetInvocationException ex) { throw new Exception(name+": "+(ex.InnerException ?? ex).Message); }
        }
        private static Component ModController(ChaControl cha,string name)
        {
            var type=OptionalType(name); if(type == null) throw new Exception("Optional plugin is not installed: " + name);
            var component=cha.GetComponent(type); if(component == null) throw new Exception("Plugin controller is not ready: " + name);
            return component;
        }
        private static JObject Capabilities(ChaControl cha)
        {
            var mods=new JObject();
            foreach(var name in new[]{BoneType,MaterialType,ResolverType}) {
                var type=OptionalType(name);
                mods[name]=new JObject { {"available",type != null},{"assembly_version",type == null ? null : type.Assembly.GetName().Version.ToString()} };
            }
            return new JObject { {"version","0.4.0"},{"mods",mods},{"design_schema",DesignSchema()},{"accessory_slots",Math.Min(20,cha.nowCoordinate.accessory.parts.Length)},{"coordinate_count",cha.chaFile.coordinate.Length},{"operation_history",128},{"limitations",new JArray("MoreAccessories slots above 19 are not exposed","Material adapter supports listed color/float/texture properties; shader replacement and packed normal conversion are not exposed","ABMX adapter targets BodyTop bones; installed custom card-load toggles still apply")} };
        }
        private static JObject AssetReference(ChaListDefine.CategoryNo category,int id)
        {
            var result=new JObject { {"category",category.ToString()},{"id",id} };
            var type=OptionalType(ResolverType); if(type == null) return result;
            try {
                var info=Invoke(type,"TryGetResolutionInfo",category,id);
                if(info != null) { result["guid"]=(string)ReadMember(info,"GUID"); result["slot"]=(int)ReadMember(info,"Slot"); }
            } catch(Exception) { /* Base-game ID remains usable even on a different Sideloader API. */ }
            return result;
        }
        private static JObject ResolveAsset(JObject p)
        {
            string key=(string)p["category"];
            if(key == null || !Enum.IsDefined(typeof(ChaListDefine.CategoryNo),key)) throw new Exception("Unknown category");
            var category=(ChaListDefine.CategoryNo)Enum.Parse(typeof(ChaListDefine.CategoryNo),key);
            int id;
            if(p["guid"] != null) {
                var type=OptionalType(ResolverType); if(type == null) throw new Exception("Sideloader is unavailable");
                int slot=Integer(p["slot"],"slot",0,int.MaxValue); string guid=(string)p["guid"];
                var infos=(IEnumerable)type.GetProperty("LoadedResolutionInfo").GetValue(null,null);
                var matches=infos.Cast<object>().Where(x=>(string)ReadMember(x,"GUID") == guid && (int)ReadMember(x,"Slot") == slot && (ChaListDefine.CategoryNo)ReadMember(x,"CategoryNo") == category).Select(x=>(int)ReadMember(x,"LocalSlot")).Distinct().ToArray();
                if(matches.Length != 1) throw new Exception("Stable asset reference is missing or ambiguous in this installation");
                id=matches[0];
            } else id=Integer(p["id"],"id",0,int.MaxValue);
            ValidateAsset(category,id); var result=AssetReference(category,id); result["name"]=AssetName(category,id); return result;
        }
        private static JObject BoneData(object modifier,ChaControl cha)
        {
            var d=Invoke(modifier,"GetModifier",(ChaFileDefine.CoordinateType)cha.chaFile.status.coordinateType);
            return new JObject { {"scale",Vec((Vector3)ReadMember(d,"ScaleModifier"))},{"length",(float)ReadMember(d,"LengthModifier")},{"position",Vec((Vector3)ReadMember(d,"PositionModifier"))},{"rotation",Vec((Vector3)ReadMember(d,"RotationModifier"))},{"coordinate_specific",(bool)Invoke(modifier,"IsCoordinateSpecific")} };
        }
        private static string[] BodyBoneNames(ChaControl cha) { return cha.objBodyBone.GetComponentsInChildren<Transform>(true).Select(t=>t.name).Distinct().OrderBy(x=>x).ToArray(); }
        private static JObject Bones(ChaControl cha,JObject p)
        {
            var controller=ModController(cha,BoneType); var location=Enum.Parse(OptionalType("KKABMX.Core.BoneLocation"),"BodyTop");
            string search=(string)p["search"] ?? ""; int offset=p["offset"] == null ? 0 : Integer(p["offset"],"offset",0,100000);
            int limit=p["limit"] == null ? 30 : Integer(p["limit"],"limit",1,100);
            var names=BodyBoneNames(cha).Where(x=>x.IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0).ToArray(); var rows=new JArray();
            foreach(var name in names.Skip(offset).Take(limit)) { var modifier=Invoke(controller,"GetModifier",name,location); rows.Add(new JObject { {"name",name},{"modified",modifier != null},{"values",modifier == null ? (JToken)JValue.CreateNull() : BoneData(modifier,cha)} }); }
            return new JObject { {"total",names.Length},{"items",rows},{"coordinate",cha.chaFile.status.coordinateType} };
        }
        private IEnumerator EditBone(ChaControl cha,JObject p,OperationContext context)
        {
            var controller=ModController(cha,BoneType); string name=(string)p["name"];
            if(name == null || !BodyBoneNames(cha).Contains(name)) throw new Exception("Unknown BodyTop bone; use list_bones");
            Vector3? scale=p["scale"] == null ? (Vector3?)null : BoundedVec(p["scale"],"scale",0.01f,5);
            Vector3? position=p["position"] == null ? (Vector3?)null : BoundedVec(p["position"],"position",-2,2);
            Vector3? rotation=p["rotation"] == null ? (Vector3?)null : BoundedVec(p["rotation"],"rotation",-180,180);
            float? length=p["length"] == null ? (float?)null : Number(p["length"],"length",0.01f,5);
            if(!scale.HasValue && !position.HasValue && !rotation.HasValue && !length.HasValue) throw new Exception("No bone changes supplied");
            bool specific=p["coordinate_specific"] == null || (bool)p["coordinate_specific"];
            yield return new WaitForEndOfFrame(); context.Backup=Backup(cha);
            var location=Enum.Parse(OptionalType("KKABMX.Core.BoneLocation"),"BodyTop");
            var modifier=Invoke(controller,"GetOrAddModifier",name,location);
            if(specific) Invoke(modifier,"MakeCoordinateSpecific",cha.chaFile.coordinate.Length);
            else Invoke(modifier,"MakeNonCoordinateSpecific",(ChaFileDefine.CoordinateType)cha.chaFile.status.coordinateType);
            var data=Invoke(modifier,"GetModifier",(ChaFileDefine.CoordinateType)cha.chaFile.status.coordinateType);
            if(scale.HasValue) WriteMember(data,"ScaleModifier",scale.Value);
            if(position.HasValue) WriteMember(data,"PositionModifier",position.Value);
            if(rotation.HasValue) WriteMember(data,"RotationModifier",rotation.Value);
            if(length.HasValue) WriteMember(data,"LengthModifier",length.Value);
            WriteMember(controller,"NeedsFullRefresh",true);
            for(int i=0;i<3;i++) yield return null;
            context.Result=new JObject { {"name",name},{"values",BoneData(modifier,cha)},{"backup_card",context.Backup} };
        }
        // Unity 5.6 lacks runtime shader property enumeration. Expose an explicit
        // typed allowlist; never infer an unknown property's type from its value.
        private static readonly string[] MaterialColors={"_Color","_Color2","_Color3","_Color4","_ShadowColor","_SpecColor","_EmissionColor","_OutlineColor"};
        private static readonly string[] MaterialFloats={"_Glossiness","_Gloss","_Metallic","_Cutoff","_BumpScale","_OcclusionStrength","_OutlineWidth"};
        private static GameObject MaterialRoot(ChaControl cha,string target,int slot)
        {
            if(target == "character") return cha.objBody;
            if(target == "head") return cha.objHead;
            GameObject[] items=target == "hair" ? cha.objHair : target == "clothes" ? cha.objClothes : target == "accessory" ? cha.objAccessory : null;
            if(items == null || slot < 0 || slot >= items.Length) throw new Exception("Target must be character, head, hair, clothes or accessory with an available slot");
            if(items[slot] == null) throw new Exception("Target slot is empty"); return items[slot];
        }
        private static JObject MaterialValues(Material material)
        {
            var colors=new JObject(); var floats=new JObject();
            foreach(var key in MaterialColors) if(material.HasProperty(key)) colors[key]=Rgba(material.GetColor(key));
            foreach(var key in MaterialFloats) if(material.HasProperty(key)) floats[key]=material.GetFloat(key);
            return new JObject { {"colors",colors},{"floats",floats},{"textures",TextureValues(material)} };
        }
        private static JObject Materials(ChaControl cha,JObject p)
        {
            string target=(string)p["target"] ?? "character"; int slot=p["slot"] == null ? 0 : Integer(p["slot"],"slot",0,19);
            var root=MaterialRoot(cha,target,slot); var rows=new JArray();
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true)) {
                var materials=renderer.materials;
                for(int i=0;i<materials.Length;i++) if(materials[i] != null) rows.Add(new JObject { {"renderer_id",renderer.GetInstanceID()},{"renderer",renderer.name},{"material_index",i},{"material",materials[i].name},{"shader",materials[i].shader.name},{"values",MaterialValues(materials[i])} });
            }
            return new JObject { {"target",target},{"slot",slot},{"items",rows},{"note","Renderer IDs are temporary: requery after any reload. Material Editor persists edits by material name within the target; equal names may share edits."} };
        }
        private IEnumerator EditMaterial(ChaControl cha,JObject p,OperationContext context)
        {
            var controller=ModController(cha,MaterialType); string target=(string)p["target"];
            int slot=p["slot"] == null ? 0 : Integer(p["slot"],"slot",0,19); var root=MaterialRoot(cha,target,slot);
            int rid=Integer(p["renderer_id"],"renderer_id",int.MinValue,int.MaxValue);
            var renderer=root.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r=>r.GetInstanceID() == rid);
            if(renderer == null) throw new Exception("Renderer expired or is outside the requested target; requery list_materials");
            var mats=renderer.materials; int index=Integer(p["material_index"],"material_index",0,mats.Length-1); var mat=mats[index];
            var colors=new Dictionary<string,Color>(); var floats=new Dictionary<string,float>();
            foreach(var group in new[]{"colors","floats"}) if(p[group] != null) {
                var values=p[group] as JObject; if(values == null) throw new Exception(group+" must be an object");
                foreach(var v in values.Properties()) {
                    if(!(group == "colors" ? MaterialColors : MaterialFloats).Contains(v.Name) || !mat.HasProperty(v.Name)) throw new Exception("Unsupported material property "+v.Name);
                    if(group == "colors") colors.Add(v.Name,ParseColor(v.Value,v.Name));
                    else floats.Add(v.Name,Number(v.Value,v.Name,-10,10));
                }
            }
            if(colors.Count == 0 && floats.Count == 0) throw new Exception("No material changes supplied");
            var objectType=Enum.Parse(controller.GetType().GetNestedType("ObjectType"),target == "hair" ? "Hair" : target == "clothes" ? "Clothing" : target == "accessory" ? "Accessory" : "Character");
            yield return new WaitForEndOfFrame(); context.Backup=Backup(cha);
            foreach(var v in colors) Invoke(controller,"SetMaterialColorProperty",slot,objectType,mat,v.Key.Substring(1),v.Value,root,true);
            foreach(var v in floats) Invoke(controller,"SetMaterialFloatProperty",slot,objectType,mat,v.Key.Substring(1),v.Value,root,true);
            yield return null;
            context.Result=new JObject { {"values",MaterialValues(mat)},{"backup_card",context.Backup} };
        }
    }
}
