# multiplayer-undo v0.1.19

Fixes restoration errors with the current Slay the Spire 2 v0.113.0, including runs using Fool.

- Clean up the outgoing map subscription before replacing the run scene, preventing callbacks into an already disposed map.
- Stop state-change notifications only while the discarded combat is being removed. Restored combat keeps its native UI notifications, gameplay effects and recorded choices.
- Preserve the current multiplayer room and final restored-state agreement.

Validation: rendered singleplayer and three-player integration, followed by exact before/after reruns of 30 Fool Standard/Blitz content and multiplayer timing seed pairs (50 undo operations). All target states and action/choice/timing traces matched. The 508 previously logged teardown exceptions dropped to zero. Existing-character checks also passed for Silent and a Defect/Silent/Silent room.

Separate-computer Steam/P2P remains unverified. This does not claim every card combination, relic trigger or potion use was tested.

**Every player must update to v0.1.19 and restart the game.**
