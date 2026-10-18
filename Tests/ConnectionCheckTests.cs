using Kot.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Kot.Tests;

public static class ConnectionCheckTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var options=new PingOptions { Mode="tcp", Url="http://check.invalid/custom-path", Attempts=2, TimeoutMs=500 };
        await using(var proxy=new Proxy("204 No Content"))
        {
            List<int?> samples=[];
            var result=await ConnectionCheck.Run(proxy.Port,options,()=>true,samples.Add,CancellationToken.None);
            check(result.Successes==2 && samples.Count==2 && proxy.Requests.Count==2 && proxy.Requests.All(r=>r.StartsWith("GET http://check.invalid/custom-path HTTP/")), "connection check uses the configured address through the supplied proxy even with TCP ping mode");
            bool changed=false;int count=0;bool rejected=false;
            try { await ConnectionCheck.Run(proxy.Port,options,()=>!changed,_=>{count++;changed=true;},CancellationToken.None); } catch(UserError){rejected=true;}
            check(rejected && count==1 && proxy.Requests.Count==3,"changed tunnel session prevents the next request");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
            try{await ConnectionCheck.Run(proxy.Port,options,()=>true,_=>{},cancelled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped && proxy.Requests.Count==3,"cancelled connection check cannot issue a request");
        }
        await using(var target=new Proxy("204 No Content"))
        {
            var unreachable=new TcpListener(IPAddress.Loopback,0);unreachable.Start();int closed=((IPEndPoint)unreachable.LocalEndpoint).Port;unreachable.Stop();
            var local=new PingOptions {Url=$"http://127.0.0.1:{target.Port}/check",Attempts=1,TimeoutMs=500};
            var result=await ConnectionCheck.Run(closed,local,()=>true,_=>{},CancellationToken.None);
            check(result.Successes==0 && target.Requests.IsEmpty,"unavailable tunnel proxy never falls back to a direct request");
            await using var redirect=new Proxy("302 Found",$"Location: http://127.0.0.1:{target.Port}/redirect\r\n");
            result=await ConnectionCheck.Run(redirect.Port,options,()=>true,_=>{},CancellationToken.None);
            check(result.Successes==0 && redirect.Requests.Count==2 && target.Requests.IsEmpty,"redirects are failed samples and cannot change the check destination");
        }
        bool current=true; int delivered=0;
        await using(var proxy=new Proxy("204 No Content",received:()=>current=false))
        {
            bool changed=false;
            try{await ConnectionCheck.Run(proxy.Port,options,()=>current,_=>delivered++,CancellationToken.None);}catch(UserError){changed=true;}
            check(changed && delivered==0 && proxy.Requests.Count==1,"session changed during HTTP request cannot publish a stale success");
        }
        foreach(int port in new[]{0,-1,65536})
        {
            bool rejected=false;try{await ConnectionCheck.Run(port,options,()=>true,_=>{},CancellationToken.None);}catch(UserError){rejected=true;}
            check(rejected,"connection check rejects a missing or invalid tunnel port");
        }
        bool invalid=false;try{ConnectionCheck.Options(new PingOptions {Mode="tcp",Url="file:///private"});}catch(UserError){invalid=true;}
        check(invalid,"connection check validates its HTTP URL independently of ping mode");
    }
    sealed class Proxy : IAsyncDisposable
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);
        readonly CancellationTokenSource lifetime=new(TimeSpan.FromSeconds(10));
        readonly Task server;
        public ConcurrentQueue<string> Requests {get;}=new();
        public int Port {get;}
        public Proxy(string status,string headers="",Action? received=null) {listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;server=Serve(status,headers,received);}
        async Task Serve(string status,string headers,Action? received)
        {
            try
            {
                while(!lifetime.IsCancellationRequested)
                {
                    using var peer=await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream=peer.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                    string first=await reader.ReadLineAsync(lifetime.Token)??"";int length=first.Length;
                    while(await reader.ReadLineAsync(lifetime.Token) is {Length:>0} line){length+=line.Length;if(length>8192)throw new Exception("Oversized fixture request");}
                    Requests.Enqueue(first);
                    received?.Invoke();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Content-Length: 0\r\nConnection: close\r\n\r\n"),lifetime.Token);
                }
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
            catch(SocketException) when(lifetime.IsCancellationRequested){}
        }
        public async ValueTask DisposeAsync(){lifetime.Cancel();listener.Stop();try{await server;}finally{lifetime.Dispose();}}
    }
}
