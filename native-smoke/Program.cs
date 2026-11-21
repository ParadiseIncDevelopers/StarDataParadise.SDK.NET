using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StarDataParadise.SDK;

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static JsonElement Json(object value) { using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value)); return doc.RootElement.Clone(); }
static async Task Read(Stream stream, byte[] bytes) { var n=0;while(n<bytes.Length){var got=await stream.ReadAsync(bytes,n,bytes.Length-n);if(got==0)throw new EndOfStreamException();n+=got;} }
static async Task<JsonElement> Receive(Stream stream) {var header=new byte[4];await Read(stream,header);var size=IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header,0));Check(size>0&&size<=1048576,"frame bound");var bytes=new byte[size];await Read(stream,bytes);using var d=JsonDocument.Parse(bytes);return d.RootElement.Clone();}
static async Task Send(Stream stream,JsonElement request,object body,bool ok=true) {var packet=ok?new Dictionary<string,object?>{{"v",1},{"id",request.GetProperty("id").GetInt32()},{"ok",true},{"body",body}}:new Dictionary<string,object?>{{"v",1},{"id",request.GetProperty("id").GetInt32()},{"ok",false},{"error",body}};var bytes=JsonSerializer.SerializeToUtf8Bytes(packet);var header=BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));await stream.WriteAsync(header,0,4);await stream.WriteAsync(bytes,0,bytes.Length);}
static string Hash(byte[] value){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(value)).Replace("-","").ToLowerInvariant();}

static async Task Boundary(Func<Stream,Task> script,Func<SdpNativeConnection,Task> test)
{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
    var server=Task.Run(async()=>{using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();
        foreach(var op in new[]{"hello","auth","capabilities","native.open"}){var request=await Receive(stream);Check(request.GetProperty("op").GetString()==op,"dedicated native handshake");object body=op switch {"hello"=>new {session_id="native-test",product_version="26.10.0",protocol_version=1},"auth"=>new {authenticated=true},"capabilities"=>new {features=new {native_sql_v1=true}},_=>new {native_sql_v1=true,autocommit=true}};await Send(stream,request,body);}await script(stream);});
    try {using var connection=await SdpNativeConnection.OpenAsync($"Host=127.0.0.1;Port={port};TLS Mode=Disabled;User=test;Password=test;Timeout=8;");await test(connection);await server;}
    finally {listener.Stop();}
}

static async Task Unit()
{
    Check(SdpFactory.Instance.CreateConnection() is SdpConnection,"legacy factory changed");
    Check(Json(SdpNativeConnection.Encode(ulong.MaxValue)).GetProperty("v").GetString()=="18446744073709551615","U64 binding");
    var decimalText="12345678901234567890123456789012345.123456789012345678901234567890";
    Check(new SdpNativeDecimal(decimalText).Scale==30,"DECIMAL65 scale");
    Check(new SdpNativeJson("{\"id\":9007199254740993}").Text.Contains("9007199254740993"),"JSON digits");
    Check(Json(SdpNativeConnection.Encode(new SdpNativeTemporal("TIME","-25:00:00.000001"))).GetProperty("v").GetString()=="-25:00:00.000001","signed interval binding");
    try{SdpNativeConnection.Encode(double.NaN);throw new Exception("NaN accepted");}catch(FormatException){}
    var raw=Json(new {sets=new[]{new {columns=new[]{new {name="id",type="BIGINT",native_type=8,nullable=false},new {name="json",type="JSON",native_type=245,nullable=false}},rows=new[]{new object[]{new {t="u64",v="18446744073709551615"},new {t="json",v="{\"id\":9007199254740993,\"padding\":\""+new string('x',650000)+"\"}"}}},affected_rows="0"}},in_transaction=true,autocommit=false});
    var payload=Encoding.UTF8.GetBytes(raw.GetRawText());var hash=Hash(payload);var id=new string('a',32);
    await Boundary(async stream=>{var request=await Receive(stream);Check(request.GetProperty("body").GetProperty("sql").GetString()=="SELECT /* :unchanged */ ?, ?","source rewritten");var offset=0;for(;;){var count=Math.Min(131072,payload.Length-offset);var part=new byte[count];Buffer.BlockCopy(payload,offset,part,0,count);await Send(stream,request,new {native_result=new {id,offset,total_bytes=payload.Length,sha256=hash,chunk=Convert.ToBase64String(part),end=offset+count==payload.Length}});offset+=count;if(offset==payload.Length)break;request=await Receive(stream);Check(request.GetProperty("op").GetString()=="native.fetch"&&request.GetProperty("body").GetProperty("offset").GetInt32()==offset,"native chunk offset");}},async connection=>{var result=await connection.ExecuteAsync("SELECT /* :unchanged */ ?, ?",new object?[]{ulong.MaxValue,new SdpNativeJson("{\"id\":9007199254740993}")});Check((ulong)result.Sets[0].Rows[0][0]! ==ulong.MaxValue,"U64 result");Check(((SdpNativeJson)result.Sets[0].Rows[0][1]!).Text.Contains("9007199254740993"),"JSON result digits");Check(result.InTransaction&&!result.Autocommit,"server transaction flags");});
    await Boundary(async stream=>{await Receive(stream);},async connection=>{try{await connection.ExecuteAsync("COMMIT");throw new Exception("lost ACK accepted");}catch(SdpException error){Check(error.UnknownOutcome&&error.RequestToken is null,"native unknown requires no legacy receipt");}});
    await Boundary(async stream=>{var request=await Receive(stream);var bytes=Encoding.UTF8.GetBytes("{}");await Send(stream,request,new {native_result=new {id=new string('c',32),offset=0,total_bytes=2,sha256=new string('0',64),chunk=Convert.ToBase64String(bytes),end=true}});},async connection=>{try{await connection.ExecuteAsync("COMMIT");throw new Exception("bad hash accepted");}catch(SdpException error){Check(error.UnknownOutcome&&!connection.IsOpen,"bad hash did not close uncertain native session");}});
    await Boundary(async stream=>{var request=await Receive(stream);await Send(stream,request,new {code=610080,native_errno=61080,sqlstate="45000",message="failed",line=(int?)null,column=(int?)null,retryable=false,unknown_outcome=true,correlation_id=(string?)null,phase="wait",commit_state="committed",run_state="failed",run_id=new string('b',32),retry=new {action="fetch"}},false);},async connection=>{try{await connection.ExecuteAsync("COMMIT");throw new Exception("error accepted");}catch(SdpException error){Check(error.Code==610080&&error.NativeErrno==61080&&error.CommitState=="committed"&&error.RunState=="failed"&&error.Retry.HasValue,"structured native error lost");}});
    await Boundary(async stream=>{var request=await Receive(stream);await Send(stream,request,new {code=510005,native_errno=61085,sqlstate="45000",message="action rolled back",line=(int?)null,column=(int?)null,retryable=false,unknown_outcome=false,correlation_id=(string?)null,phase="tool_execution",commit_state="committed",action_commit_state="rolled_back",run_state="failed",run_id=new string('b',32),call_id=new string('d',32),cause_native_errno=1644,cause_sqlstate="45000"},false);},async connection=>{try{await connection.ExecuteAsync("COMMIT");throw new Exception("action failure accepted");}catch(SdpException error){Check(error.Code==510005&&error.NativeErrno==61085&&error.CommitState=="committed"&&error.ActionCommitState=="rolled_back"&&!error.UnknownOutcome&&error.CallId==new string('d',32)&&error.CauseNativeErrno==1644&&error.CauseSqlstate=="45000"&&connection.IsOpen,"initial commit and action rollback diagnostics were conflated");}});
    Console.WriteLine("PASS native .NET boundary: exact values, chunks/hash, untouched SQL, transaction flags, uncertain ACK, structured errors; legacy factory preserved");
}

static async Task Integration(string options)
{
    using var connection=await SdpNativeConnection.OpenAsync(options);
    var result=await connection.ExecuteAsync("SELECT CAST(? AS UNSIGNED),CAST(? AS DECIMAL(65,30)),CAST(? AS JSON),?",new object?[]{ulong.MaxValue,new SdpNativeDecimal("12345678901234567890123456789012345.123456789012345678901234567890"),new SdpNativeJson("{\"id\":9007199254740993}"),new byte[]{0,1,254,255}});
    Check((ulong)result.Sets[0].Rows[0][0]! ==ulong.MaxValue,"native U64");Check(((SdpNativeDecimal)result.Sets[0].Rows[0][1]!).Scale==30,"native DECIMAL");Check(((SdpNativeJson)result.Sets[0].Rows[0][2]!).Text.Contains("9007199254740993"),"native JSON");Check(((byte[])result.Sets[0].Rows[0][3]!).SequenceEqual(new byte[]{0,1,254,255}),"native BLOB");
    foreach(var duration in new[]{"-01:02:03.000004","25:00:00","-00:00:00.000001"})
    {
        var interval=await connection.ExecuteAsync("SELECT CAST(CAST(? AS CHAR) AS TIME(6))",new object?[]{duration});
        var actual=((SdpNativeTemporal)interval.Sets[0].Rows[0][0]!).Text;
        Check(actual==duration,"native signed TIME interval: "+duration+" => "+actual);
    }
    await connection.ExecuteAsync("DROP PROCEDURE IF EXISTS sdk_native_echo");
    await connection.ExecuteAsync("CREATE PROCEDURE sdk_native_echo(IN p BIGINT,OUT result BIGINT) SQL SECURITY INVOKER BEGIN SET result=p+1;SELECT result AS emitted;SELECT CONCAT('echo:',p) AS note;END");
    var echoed=await connection.ExecuteAsync("CALL sdk_native_echo(?,@sdk_native_out)",new object?[]{7L});
    Check(echoed.Sets.Count(s=>s.Rows.Count>0)>=2&&echoed.Sets.Any(s=>s.Rows.Count>0&&Convert.ToString(s.Rows[0][0])=="8"),"native prepared CALL multiple result sets");
    var outValue=await connection.ExecuteAsync("SELECT @sdk_native_out");Check(Convert.ToString(outValue.Sets[0].Rows[0][0])=="8","native CALL OUT value");
    await connection.ExecuteAsync("DROP PROCEDURE sdk_native_echo");
    await connection.ExecuteAsync("DROP PROCEDURE IF EXISTS native_text_out");
    await connection.ExecuteAsync("CREATE PROCEDURE native_text_out(OUT q VARCHAR(32) CHARACTER SET utf8mb4) SQL SECURITY INVOKER SET q='Türkçe'");
    var textOut=await connection.ExecuteAsync("CALL native_text_out(?)",new object?[]{new byte[]{255}});
    Check(textOut.Sets.Any(s=>s.Rows.Any(row=>row.Count>0&&row[0] is string text&&text=="Türkçe")),"native declared text OUT metadata must replace binary input metadata");
    await connection.ExecuteAsync("DROP PROCEDURE native_text_out");
    await connection.ExecuteAsync("CREATE TEMPORARY TABLE sdk_native_probe(id BIGINT PRIMARY KEY) ENGINE=InnoDB");await connection.BeginAsync();await connection.ExecuteAsync("INSERT INTO sdk_native_probe VALUES(?)",new object?[]{1L});await connection.RollbackAsync();var rolled=await connection.ExecuteAsync("SELECT COUNT(*) FROM sdk_native_probe");Check((long)rolled.Sets[0].Rows[0][0]! ==0,"native rollback");
    var large=await connection.ExecuteAsync("SELECT JSON_OBJECT('id',CAST('9007199254740993' AS UNSIGNED),'padding',REPEAT('x',800000))");Check(((SdpNativeJson)large.Sets[0].Rows[0][0]!).Text.Contains("9007199254740993"),"native chunked JSON");
    Console.WriteLine("PASS actual native SQL/TLS .NET prepared values, signed TIME, CALL/OUT/multiple sets, transaction rollback and large JSON");
}

await Unit();
var options=Environment.GetEnvironmentVariable("SDP_NATIVE_CONNECTION");
if(!string.IsNullOrEmpty(options))await Integration(options);else Console.WriteLine("NOT_RUN actual native SQL/TLS (SDP_NATIVE_CONNECTION missing)");
