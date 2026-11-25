using System;
using System.IO;
using System.Reflection;
class ProbeChecks {
 static void Main(string[] args) {
  var root=args[0]; var a=Assembly.LoadFrom(Path.Combine(root,"AtasReplayProbe.dll"));
  var t=a.GetType("AtasReplayProbe.Program")!; var flags=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
  object Call(string name,params object[] values)=>t.GetMethod(name,flags)!.Invoke(null,values)!;
  var d=Path.Combine(root,"mixed_progress"); Directory.CreateDirectory(d);
  File.WriteAllText(Path.Combine(d,"ticks.ndjson"),"{\"tMarket\":\"2026-10-26T08:00:03Z\"}\n");
  File.WriteAllText(Path.Combine(d,"cumtrades.ndjson"),"{\"tMarket\":\"2026-10-30T12:00:00Z\"}\n");
  var end=new DateTime(2026,10,26,8,1,0,DateTimeKind.Utc);
  var streams=Call("FolderStreams",d); var mp=Call("MarketProgress",streams);
  Console.WriteLine("historical_ticks_at_08_00_03_plus_live_cumulative: EndReached="+Call("EndReached",mp,end));
  Console.WriteLine("end_minus_one_second: EndReached="+Call("EndReached",end.AddSeconds(-1),end));
  File.WriteAllText(Path.Combine(d,"cumtrades.ndjson"),"{\"tMarket\":\"2026-10-26T08:03:00Z\"}\n");
  Console.WriteLine("later_preload_timestamp: EndReached="+Call("EndReached",Call("MarketProgress",Call("FolderStreams",d)),end));
  Console.WriteLine("one_foreign_MNQ_candidate: verdict="+Call("AttributionVerdict",1));
 }
}
