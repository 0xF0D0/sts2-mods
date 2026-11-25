using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using UndoSync;
namespace MinimalHarness;

internal static class PeerSkewProbe
{
    private static Task? _play;
    private static bool _watch;
    private static GameAction? _action;
    [HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.EnqueueWithoutSynchronizing))]
    private static class PlayObserver
    {
        private static void Postfix(GameAction gameAction)
        {
            if (StartupQueueProbe.Skew && _watch && !Network.Executing && gameAction is PlayCardAction && gameAction.OwnerId == Harness.State.Players[2].NetId)
            {
                _action=gameAction; _play=gameAction.CompletionTask;
                Harness.Log("PEER_SKEW_ACTION native=true id="+gameAction.Id);
            }
        }
    }
    private static async Task Choose()
    {
        await Harness.Wait(() => NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen);
        ulong ready = Time.GetTicksMsec()+400;
        await Harness.Wait(() => Time.GetTicksMsec()>=ready);
        var screen=(NChooseACardSelectionScreen)NOverlayStack.Instance!.Peek()!;
        var holder=screen.GetNode<Control>("CardRow").GetChildren().OfType<NGridCardHolder>().First();
        await Harness.ClickViaViewport(holder.Hitbox,"peer-skew-potion-choice");
        Harness.RecordSelection();
        await Harness.Wait(() => NOverlayStack.Instance.Peek() is not NChooseACardSelectionScreen);
    }
    internal static async Task Run()
    {
        Harness.Require(Harness.Roles.Length==3,"heterogeneous skew requires three real peers");
        await Harness.Actor("skew-target",Harness.Roles[0]);
        int target=History.Count;string expected=Harness.Snapshot();int pendingVotes=0;
        int earlyTarget=target;string earlyState=expected;
        var targets=new List<object>();
        for(int i=0;i<3;i++)
        {
            target=earlyTarget;expected=earlyState;
            if(i==1 && MegaCrit.Sts2.Core.Helpers.CommandLineHelper.HasArg("minimal-skew-varied"))
            {
                await Harness.Actor("skew-later-target",Harness.Roles[1]);
                target=History.Count;expected=Harness.Snapshot();
            }
            targets.Add(new {target,expected});
            await Harness.Actor("skew-mutate-"+i,Harness.Roles[0]);
            var me=LocalContext.GetMe(Harness.State)!;
            me.Potions.Single(p=>p.Id.Entry=="SKILL_POTION").EnqueueManualUse(me.Creature);
            await Harness.Wait(()=>NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen);
            await Harness.Barrier("skew-all-open-"+i);
            if(Harness.Role!=Harness.Roles[1]) await Choose();
            await Harness.Barrier("skew-partial-"+i);
            Harness.Require((NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen)==(Harness.Role==Harness.Roles[1]),"one real peer still choosing while other two have completed");
            string proposer=Harness.Roles[i];
            string final=proposer==Harness.Roles[2]?Harness.Roles[0]:Harness.Roles[2];
            _watch=true;_play=null;_action=null;
            await Harness.Barrier("skew-before-action-"+i);
            if(Harness.Role==Harness.Roles[2])
            {
                var card=PileType.Hand.GetPile(me).Cards.First(c=>c.Id.Entry.StartsWith("STRIKE_")&&c.CanPlay());
                var enemy=CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.First();
                Harness.Require(card.TryManualPlay(enemy),"skew native input accepted before proposal while peer choice remains open");
            }
            await Harness.Wait(()=>_action?.State==GameActionState.Executing && _play is {IsCompleted:false},10);
            if(Harness.Role==proposer)Network.Request(target);
            await Harness.Wait(()=>Network.Voting,10);
            if(Harness.Role!=proposer&&Harness.Role!=final)Network.Vote(true);
            if(Harness.Role==final)
            {
                // Read-only host log evidence ensures this really is the final
                // vote, rather than assuming local send means host receipt.
                await Harness.Wait(()=>
                {
                    string log=File.ReadAllText(Path.Combine(Harness.DirectoryPath,"host-terminal.log"));
                    int at=log.LastIndexOf("PROPOSE session=",StringComparison.Ordinal);
                    return at>=0&&log[at..].Contains("received=2/3");
                },10);
                Harness.Require(!_play!.IsCompleted,"native action genuinely pending at final vote");
                Network.Vote(true);pendingVotes++;
            }
            Harness.Log("PEER_SKEW_BEFORE_FINAL actionPending=true chooser="+Harness.Roles[1]+" proposer="+proposer);
            await Harness.Wait(()=>!Network.Busy,60);_watch=false;
            Harness.Require(Network.Status=="Undo complete","peer skew restoration terminal success");
            await Harness.Idle();
            Harness.Require(History.Count==target&&Harness.Snapshot()==expected,"peer skew exact target and original potion inventory restored");
            Harness.Require(NOverlayStack.Instance?.Peek() is not NChooseACardSelectionScreen,"old pending peer choice view removed");
            foreach(var c in PileType.Hand.GetPile(LocalContext.GetMe(Harness.State)!).Cards)
            {
                var h=NCombatRoom.Instance!.Ui.Hand.GetCardHolder(c);
                Harness.Require(h?.CardNode?.Model==c&&h.CardNode.IsVisibleInTree(),"skew current native hand binding");
            }
            Harness.RecordUndo();
            await Harness.StateBarrier("skew-restored-"+i);
            if(DisplayServer.GetName()!="headless")
            {
                RenderingServer.ForceDraw();
                NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath,$"skew-{i}-{Harness.Role}.png"));
            }
        }
        StartupQueueProbe.WriteCompositionProof();
        await Harness.Actor("skew-continued",Harness.Roles[2]);
        await Harness.EndTurnAll("skew-next-turn");
        await Harness.CheckConnectedUi("skew-final");
        File.WriteAllText(Path.Combine(Harness.DirectoryPath,$"skew-proof.{Harness.Role}.json"),JsonSerializer.Serialize(new {target,expected,targets,restores=3,pending_votes=pendingVotes,characters=Harness.State.Players.Select(p=>p.Character.Id.Entry).ToArray()}));
        Harness.Log("PEER_SKEW_PASS");
        await Harness.Barrier("skew-terminal");
    }
}
