using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KKMakerBridge
{
    public sealed partial class MakerBridge
    {
        private readonly Dictionary<string,Work> operations = new Dictionary<string,Work>();
        private readonly Queue<string> operationOrder = new Queue<string>();
        private Work RegisterWork(JObject request)
        {
            string id = (string)request["request_id"] ?? Guid.NewGuid().ToString("N");
            if (id.Length < 1 || id.Length > 80 || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new Exception("Invalid request_id");
            lock(queue) {
                Work old;
                if(operations.TryGetValue(id,out old)) {
                    if((string)old.Request["method"] != (string)request["method"] || !JToken.DeepEquals(old.Request["params"],request["params"])) throw new Exception("request_id already used with different arguments");
                    return old;
                }
                if(queue.Count >= 8) throw new Exception("Bridge busy");
                while(operations.Count >= 128 && operationOrder.Count > 0) {
                    var first=operations[operationOrder.Peek()];
                    lock(first.Gate) { if(first.State < 2) throw new Exception("Operation journal full; wait for outstanding work"); }
                    operations.Remove(operationOrder.Dequeue());
                }
                var work = new Work { Id=id,Request=request };
                operations.Add(id,work); operationOrder.Enqueue(id); queue.Enqueue(work); return work;
            }
        }
        private JObject OperationStatus(string id)
        {
            Work work;
            lock(queue) { if(id == null || !operations.TryGetValue(id,out work)) throw new Exception("Operation not found in this game session (last 128 requests retained)"); }
            lock(work.Gate) return new JObject { {"operation_id",id},{"method",(string)work.Request["method"]},{"state",new[]{"queued","running","completed","canceled"}[work.State]}, {"backup_card",work.Context.Backup},{"response",work.Response == null ? JValue.CreateNull() : work.Response.DeepClone()} };
        }
        private static string LocalKey(JToken raw)
        {
            string key = (string)raw;
            if(string.IsNullOrEmpty(key) || key.Length > 64 || key.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) throw new Exception("Use a name/id of 1..64 letters, digits, underscores or hyphens");
            return key;
        }
        private JObject ListSnapshots()
        {
            string dir=Path.Combine(runtime,"snapshots");
            return new JObject { {"items",new JArray(Directory.Exists(dir) ? Directory.GetFiles(dir,"*.json").OrderByDescending(x=>x).Take(100).Select(x=>JObject.Parse(File.ReadAllText(x))) : Enumerable.Empty<JObject>())} };
        }
        private IEnumerator Snapshot(ChaControl cha,JObject p,OperationContext context)
        {
            string name=p["name"] == null ? "snapshot" : LocalKey(p["name"]);
            yield return new WaitForEndOfFrame();
            var card=SaveCard(cha,new JObject { {"name",name+"_"+Guid.NewGuid().ToString("N")} });
            string id=DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"_"+Guid.NewGuid().ToString("N");
            var snapshot=new JObject { {"id",id},{"name",name},{"created_utc",DateTime.UtcNow.ToString("o")},{"card",card["path"]},{"camera",CameraInfo()},{"coordinate",cha.chaFile.status.coordinateType},{"design",DesignValues(cha)},{"accessories",Accessories(cha)["items"]} };
            string dir=Path.Combine(runtime,"snapshots"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir,id+".json"),snapshot.ToString()); context.Result=snapshot;
        }
        private IEnumerator RestoreSnapshot(ChaControl cha,JObject p,OperationContext context)
        {
            string id=LocalKey(p["id"]);
            var snapshot=JObject.Parse(File.ReadAllText(Path.Combine(runtime,"snapshots/"+id+".json")));
            int coordinate=Integer(snapshot["coordinate"],"coordinate",0,cha.chaFile.coordinate.Length-1);
            yield return LoadCard(cha,new JObject { {"path",snapshot["card"]} },context);
            if(cha.chaFile.status.coordinateType != coordinate) {
                if(!cha.ChangeCoordinateType((ChaFileDefine.CoordinateType)coordinate,true)) throw new Exception("Snapshot coordinate could not be selected");
                yield return cha.ReloadAsync(false,true,true,true,false);
            }
            SetCamera((JObject)snapshot["camera"]);
            // Translation plugins may serialize native names, then present translated
            // names after saving. A snapshot also preserves the visible identity.
            var identity=(JObject)snapshot["design"]["identity"];
            cha.chaFile.parameter.firstname=(string)identity["firstname"];
            cha.chaFile.parameter.lastname=(string)identity["lastname"];
            cha.chaFile.parameter.nickname=(string)identity["nickname"];
            KKAPI.Maker.MakerAPI.GetMakerBase().customCtrl.UpdateCharaNameText();
            var verification=(JObject)VerifyCard(cha,new JObject { {"path",snapshot["card"]} });
            // Snapshot IDs can outlive a game launch, so verify design asset IDs
            // against the freshly resolved card rather than the old runtime IDs.
            context.Result=new JObject { {"snapshot_id",id},{"backup_card",context.Backup},{"verification",verification},{"camera",CameraInfo()}, {"note","Card and camera restored. Exposed card fields verified; extended mod state must be inspected with its adapter."} };
        }
        private JObject CameraPreset(JObject p)
        {
            string action=(string)p["action"] ?? "list"; string dir=Path.Combine(runtime,"camera-presets");
            if(action == "list") return new JObject { {"names",new JArray(Directory.Exists(dir) ? Directory.GetFiles(dir,"*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x=>x) : Enumerable.Empty<string>())} };
            if(action != "save" && action != "load") throw new Exception("action must be list, save or load");
            string name=LocalKey(p["name"]); string path=Path.Combine(dir,name+".json");
            if(action == "save") { Directory.CreateDirectory(dir); File.WriteAllText(path,CameraInfo().ToString()); }
            else SetCamera(JObject.Parse(File.ReadAllText(path)));
            return new JObject { {"name",name},{"camera",CameraInfo()} };
        }
        private IEnumerator CaptureViews(JObject p,OperationContext context)
        {
            Character(); // Require Maker before touching its camera.
            var original=CameraInfo(); var captures=new JArray();
            var angles=p["yaw_offsets"] as JArray ?? new JArray(-30,0,30);
            if(angles.Count < 1 || angles.Count > 8) throw new Exception("Provide 1..8 yaw offsets");
            var offsets=angles.Select(x=>Number(x,"yaw_offset",-180,180)).ToArray();
            // Validate dimensions before any camera movement.
            Number(p["width"] ?? new JValue(1280),"width",256,2048); Number(p["height"] ?? new JValue(720),"height",256,2048);
            try {
                foreach(float offset in offsets) {
                    var rotation=(JArray)original["rotation"].DeepClone();
                    float yaw=((float)rotation[1]+offset)%360; rotation[1]=yaw;
                    SetCamera(new JObject { {"rotation",rotation} });
                    yield return null; yield return new WaitForEndOfFrame();
                    var shot=(JObject)Capture(p); shot["yaw_offset"]=offset; captures.Add(shot);
                }
                context.Result=new JObject { {"captures",captures},{"camera_restored",true} };
            } finally { SetCamera(original); }
        }
    }
}
