using Kot.Core;
using System.Text.Json;
namespace Kot.Tests;

public static class SubscriptionEditingTests
{
    public static void Run(Action<bool, string> check)
    {
        var node = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls#Node");
        var p = new Profile { Subscriptions = [
            new() { Id="a", Name="First", Address="https://example.com/a", Nodes=[node], Selected=node.Id, Favorites=[node.Id], Updated=DateTimeOffset.UtcNow },
            new() { Id="b", Name="Second", Address="https://example.com/b", Nodes=[node], Selected="auto", Favorites=[node.Id] },
            new() { Id="local", Name="Local", Local=true, Nodes=[node], Selected=node.Id }
        ] };
        p.Activate("a");
        p.EditSubscription("b", " Renamed ", "https://example.com/new"); p.CaptureActive(); p.Validate();
        check(p.ActiveSubscription=="a" && p.Name=="First" && p.Subscriptions[1].Name=="Renamed" && p.Subscriptions[1].Address=="https://example.com/new" && p.Subscriptions[1].Selected=="auto" && p.Subscriptions[1].Favorites.SequenceEqual([node.Id]), "editing inactive subscription preserves active connection selection, nodes and favorites");
        p.EditSubscription("a", "Active edit", "https://example.com/changed"); p.CaptureActive(); p.Validate();
        check(p.Name=="Active edit" && p.Address=="https://example.com/changed" && p.Subscriptions[0].Name==p.Name && p.Updated==null && p.Nodes.Single().Id==node.Id && p.Favorites.SequenceEqual([node.Id]), "active subscription edit survives capture/validation and resets stale source metadata");
        foreach(var (id,name,url) in new[] { ("a","","https://example.com/x"), ("a",new string('x',61),"https://example.com/x"), ("a","Name","http://example.com/x"), ("a","Name","https://u:p@example.com/x"), ("a","Name","https://example.com/x#secret"), ("a","Name","https://example.com/new"), ("local","Name","https://example.com/x"), ("missing","Name","") })
        {
            var before=JsonSerializer.Serialize(p); bool failed=false;
            try { p.EditSubscription(id,name,url); } catch(UserError) { failed=true; }
            check(failed && JsonSerializer.Serialize(p)==before,"invalid subscription edits leave the entire profile unchanged");
        }
        p.EditSubscription("local","Offline","");
        check(p.Subscriptions[2].Local && p.Subscriptions[2].Name=="Offline", "local profile can be renamed without converting it to a subscription");
        p.RemoveSubscription("b"); p.CaptureActive(); p.Validate();
        check(p.ActiveSubscription=="a" && p.Subscriptions.Count==2 && p.Selected==node.Id,"removing inactive profile preserves active profile");
        bool missing=false;try {p.RemoveSubscription("b");}catch(UserError){missing=true;}
        check(missing && p.Subscriptions.Count==2,"duplicate removal cannot delete another profile");
        p.RemoveSubscription("a"); p.CaptureActive(); p.Validate();
        check(p.ActiveSubscription=="local" && p.Local && p.Name=="Offline", "removing active profile activates an existing survivor");
        p.RemoveSubscription("local"); p.CaptureActive(); p.Validate();
        check(p.Subscriptions.Count==0 && p.Nodes.Count==0 && p.ActiveSubscription=="" && p.Address=="" && !p.Local,"last profile removal cannot resurrect captured active data");
    }
}
