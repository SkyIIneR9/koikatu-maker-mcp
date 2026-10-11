using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KKMakerBridge
{
    public sealed partial class MakerBridge
    {
        private const int TextureByteLimit = 16 * 1024 * 1024;
        private static readonly string[] TextureProperties = { "_MainTex", "_MainTex2", "_MainTex3", "_DetailMainTex", "_BumpMap", "_NormalMap", "_DetailNormalMap", "_ColorMask", "_ColorMask2", "_DetailMask", "_Mask", "_MaskTex", "_AlphaMask", "_EmissionMap", "_OcclusionMap", "_MetallicGlossMap", "_SpecGlossMap", "_DetailGlossMap", "_LineMask", "_OverlayTex" };

        private sealed class MaterialSelection
        {
            public GameObject Root;
            public Material Material;
            public int Slot;
            public Component Controller;
            public object ObjectType;
            public string RendererName;
            public int MaterialIndex;
        }
        private static MaterialSelection SelectMaterial(ChaControl cha, JObject p, bool needController)
        {
            string target=(string)p["target"];
            int slot=p["slot"] == null ? 0 : Integer(p["slot"],"slot",0,19);
            var root=MaterialRoot(cha,target,slot);
            if(root == null) throw new Exception("Material target is not loaded");
            int id=Integer(p["renderer_id"],"renderer_id",int.MinValue,int.MaxValue);
            var renderer=root.GetComponentsInChildren<Renderer>(true).FirstOrDefault(x=>x.GetInstanceID() == id);
            if(renderer == null) throw new Exception("Renderer expired or belongs to another target; requery list_materials");
            var materials=renderer.materials;
            int index=Integer(p["material_index"],"material_index",0,materials.Length-1);
            var result=new MaterialSelection { Root=root, Material=materials[index], Slot=slot, RendererName=renderer.name, MaterialIndex=index };
            if(result.Material == null) throw new Exception("Material slot is empty");
            if(needController) {
                result.Controller=ModController(cha,MaterialType);
                result.ObjectType=Enum.Parse(result.Controller.GetType().GetNestedType("ObjectType"),target == "hair" ? "Hair" : target == "clothes" ? "Clothing" : target == "accessory" ? "Accessory" : "Character");
            }
            return result;
        }
        private static string TextureProperty(Material material, JToken raw)
        {
            string property=(string)raw;
            if(!TextureProperties.Contains(property) || !material.HasProperty(property)) throw new Exception("Use a supported texture property returned by list_materials");
            return property;
        }
        private static JObject TextureInfo(Material material, string property)
        {
            Texture tex=material.GetTexture(property); Vector2 scale=material.GetTextureScale(property), offset=material.GetTextureOffset(property);
            return new JObject { {"assigned",tex != null},{"name",tex == null ? null : tex.name},{"width",tex == null ? 0 : tex.width},{"height",tex == null ? 0 : tex.height},{"texture_type",tex == null ? null : tex.GetType().Name},{"scale",new JArray(scale.x,scale.y)},{"offset",new JArray(offset.x,offset.y)} };
        }
        private static JObject TextureValues(Material material)
        {
            var result=new JObject();
            foreach(string property in TextureProperties) if(material.HasProperty(property)) result[property]=TextureInfo(material,property);
            return result;
        }
        private static string HashBytes(byte[] bytes)
        {
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }
        private string TexturePath(string name)
        {
            if(name == null || name.Length != 68 || !name.EndsWith(".png",StringComparison.Ordinal) || name.Substring(0,64).Any(c=>!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))) throw new Exception("texture must be the content-hash filename returned by import_texture");
            string dir=Path.Combine(runtime,"textures"); string path=Path.Combine(dir,name);
            RejectLinks(path,runtime);
            if(!File.Exists(path)) throw new Exception("Staged texture not found; import it first");
            return path;
        }
        private static uint PngInteger(byte[] bytes,int offset)
        { return ((uint)bytes[offset]<<24)|((uint)bytes[offset+1]<<16)|((uint)bytes[offset+2]<<8)|bytes[offset+3]; }
        private byte[] TextureBytes(string name, out int width, out int height)
        {
            string path=TexturePath(name); byte[] bytes;
            using(var file=File.OpenRead(path)) {
                if(file.Length < 33 || file.Length > TextureByteLimit) throw new Exception("PNG must be 33 bytes..16 MiB");
                bytes=new byte[(int)file.Length]; int n=0;
                while(n < bytes.Length) { int read=file.Read(bytes,n,bytes.Length-n); if(read == 0) throw new Exception("Truncated texture"); n+=read; }
            }
            if(!new byte[]{137,80,78,71,13,10,26,10}.SequenceEqual(bytes.Take(8)) || PngInteger(bytes,8) != 13 || System.Text.Encoding.ASCII.GetString(bytes,12,4) != "IHDR") throw new Exception("Invalid PNG header");
            uint w=PngInteger(bytes,16),h=PngInteger(bytes,20);
            if(w < 1 || w > 4096 || h < 1 || h > 4096) throw new Exception("PNG dimensions must be 1..4096");
            if(HashBytes(bytes)+".png" != name) throw new Exception("Texture content no longer matches its imported hash");
            // Decode before creating a backup or mutating any character state.
            Texture2D probe=null;
            try {
                probe=new Texture2D(2,2,TextureFormat.RGBA32,false);
                if(!probe.LoadImage(bytes) || probe.width != (int)w || probe.height != (int)h) throw new Exception("Unity could not decode the PNG");
            } finally { if(probe != null) UnityEngine.Object.Destroy(probe); }
            width=(int)w; height=(int)h; return bytes;
        }
        private JObject InspectTexture(JObject p)
        {
            string name=(string)p["texture"]; int width,height; var bytes=TextureBytes(name,out width,out height);
            return new JObject { {"texture",name},{"width",width},{"height",height},{"bytes",bytes.Length},{"sha256",name.Substring(0,64)},{"path",TexturePath(name)},{"decoded",true} };
        }
        private JObject ListTextures(JObject p)
        {
            int offset=p["offset"] == null ? 0 : Integer(p["offset"],"offset",0,100000);
            int limit=p["limit"] == null ? 30 : Integer(p["limit"],"limit",1,100);
            string dir=Path.Combine(runtime,"textures"); RejectLinks(dir,runtime);
            var rows=Directory.Exists(dir) ? Directory.GetFiles(dir,"*.png").Where(x=>(File.GetAttributes(x)&FileAttributes.ReparsePoint) == 0).OrderBy(x=>x).Skip(offset).Take(limit+1).ToArray() : new string[0];
            return new JObject { {"items",new JArray(rows.Take(limit).Select(x=>new JObject { {"texture",Path.GetFileName(x)},{"bytes",new FileInfo(x).Length} }))},{"has_more",rows.Length > limit},{"note","Files are verified and decoded again before applying. Import/list do not edit the character."} };
        }
        private JObject ExportTexture(ChaControl cha,JObject p)
        {
            var selected=SelectMaterial(cha,p,false); string property=TextureProperty(selected.Material,p["property"]);
            Texture source=selected.Material.GetTexture(property);
            if(source == null || (source as Texture2D == null && source as RenderTexture == null)) throw new Exception("Selected property has no exportable 2D texture");
            if(source.width < 1 || source.height < 1 || source.width > 4096 || source.height > 4096) throw new Exception("Export supports textures up to 4096 per side");
            Texture2D readable=null; RenderTexture render=null; var previous=RenderTexture.active;
            try {
                render=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
                Graphics.Blit(source,render); RenderTexture.active=render;
                readable=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);
                readable.ReadPixels(new Rect(0,0,source.width,source.height),0,0); readable.Apply();
                byte[] bytes=readable.EncodeToPNG(); string dir=Path.Combine(runtime,"texture-exports"); RejectLinks(dir,runtime); Directory.CreateDirectory(dir);
                string path=Path.Combine(dir,"texture_"+Guid.NewGuid().ToString("N")+".png"); File.WriteAllBytes(path,bytes);
                return new JObject { {"path",path},{"property",property},{"width",source.width},{"height",source.height},{"sha256",HashBytes(bytes)},{"note","GPU readback of the current texture. Color-space conversion may occur; packed normal maps are not converted to editable tangent-space normals."} };
            } finally {
                RenderTexture.active=previous;
                if(render != null) RenderTexture.ReleaseTemporary(render);
                if(readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
        private static Vector2 TextureVector(JToken token,string name,float min,float max)
        {
            var a=token as JArray; if(a == null || a.Count != 2) throw new Exception(name+" must have two numbers");
            return new Vector2(Number(a[0],name,min,max),Number(a[1],name,min,max));
        }
        private IEnumerator EditTexture(ChaControl cha,JObject p,OperationContext context)
        {
            var s=SelectMaterial(cha,p,true); string property=TextureProperty(s.Material,p["property"]); string key=property.Substring(1);
            string action=(string)p["action"] ?? "set";
            if(action != "set" && action != "reset") throw new Exception("action must be set or reset");
            byte[] data=null; int width=0,height=0;
            if(p["texture"] != null) data=TextureBytes((string)p["texture"],out width,out height);
            Vector2? offset=p["offset"] == null ? (Vector2?)null : TextureVector(p["offset"],"offset",-100,100);
            Vector2? scale=p["scale"] == null ? (Vector2?)null : TextureVector(p["scale"],"scale",-100,100);
            if(scale.HasValue && (Math.Abs(scale.Value.x)<0.001f || Math.Abs(scale.Value.y)<0.001f)) throw new Exception("Texture scale components must be nonzero (absolute value >= 0.001)");
            if(action == "set" && data == null && !offset.HasValue && !scale.HasValue) throw new Exception("Provide texture, scale or offset");
            if(action == "reset" && (data != null || offset.HasValue || scale.HasValue)) throw new Exception("reset cannot be combined with texture/scale/offset");
            yield return new WaitForEndOfFrame(); context.Backup=Backup(cha);
            if(action == "reset") {
                var camera=CameraInfo();
                Invoke(s.Controller,"RemoveMaterialTexture",s.Slot,s.ObjectType,s.Material,key,s.Root,false);
                Invoke(s.Controller,"RemoveMaterialTextureOffset",s.Slot,s.ObjectType,s.Material,key,s.Root,true);
                Invoke(s.Controller,"RemoveMaterialTextureScale",s.Slot,s.ObjectType,s.Material,key,s.Root,true);
                // This mod removes persisted overrides but cannot restore the source
                // texture in-place. Serialize the cleared state and use its load hooks.
                string resetCard=Backup(cha);
                var reload=new OperationContext();
                try { yield return LoadCard(cha,new JObject { {"path",resetCard} },reload); }
                finally { SetCamera(camera); }
                var root=MaterialRoot(cha,(string)p["target"],s.Slot);
                var matches=root.GetComponentsInChildren<Renderer>(true).Where(r=>r.name == s.RendererName).ToArray();
                if(matches.Length != 1 || s.MaterialIndex >= matches[0].materials.Length) throw new Exception("Reset reloaded, but the material cannot be identified unambiguously; inspect backup_card and list_materials");
                var material=matches[0].materials[s.MaterialIndex]; var controller=ModController(cha,MaterialType);
                if(Invoke(controller,"GetMaterialTexture",s.Slot,s.ObjectType,material,key,root) != null ||
                   Invoke(controller,"GetMaterialTextureOffset",s.Slot,s.ObjectType,material,key,root) != null ||
                   Invoke(controller,"GetMaterialTextureScale",s.Slot,s.ObjectType,material,key,root) != null)
                    throw new Exception("Texture/UV override remains after reset; check Material Editor load toggles and backup_card");
                context.Result=new JObject { {"action","reset"},{"property",property},{"backup_card",context.Backup},{"reloaded",true},{"overrides_cleared",true},{"values",TextureInfo(material,property)},{"note","Texture and UV overrides cleared. Renderer IDs expired; requery list_materials. Custom mod load toggles remain in effect."} };
                yield break;
            }
            if(data != null) Invoke(s.Controller,"SetMaterialTexture",s.Slot,s.ObjectType,s.Material,key,data,s.Root);
            if(offset.HasValue) Invoke(s.Controller,"SetMaterialTextureOffset",s.Slot,s.ObjectType,s.Material,key,offset.Value,s.Root,true);
            if(scale.HasValue) Invoke(s.Controller,"SetMaterialTextureScale",s.Slot,s.ObjectType,s.Material,key,scale.Value,s.Root,true);
            for(int i=0;i<3;i++) yield return null;
            var info=TextureInfo(s.Material,property);
            if(data != null && ((int)info["width"] != width || (int)info["height"] != height)) throw new Exception("Material did not accept the texture dimensions; inspect state and backup_card");
            context.Result=new JObject { {"action","set"},{"property",property},{"texture",p["texture"]},{"values",info},{"backup_card",context.Backup} };
        }
    }
}
