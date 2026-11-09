# multiplayer-undo v0.1.15

- Reconstruct and replay the native combat model without combat UI, then build and bind the restored UI once.
- Preserve native actions, pile notifications and gameplay hooks, damage, RNG and recorded choices. Skip replay presentation without enabling TestMode or changing engine time scale.
- Restore the hand, health, enemy intents, End Turn button, map and current orb displays. Preserve the existing multiplayer room and final checksum/resume barriers.
- Retain control-message acknowledgements/retries and failure diagnostics from v0.1.14.

Measured local three-player ENet test, identical 500-action Finesse record and target state: maximum per-peer restoration-start to input-ready time **159.020s -> 1.024s**. This includes model reconstruction, playback, UI binding, final checksum consensus and input release; voting and fixture preparation are excluded. One workload and one trial per variant, not a guarantee for every fight or network.

Validation: real-game UI checks for repeated undo, generated cards, hand transformations, map and native orb channel/evoke; final three-process choices/enemy-turn integration; three timing-fuzz seeds with nine distinct three-peer undo operations; injected replay-failure propagation and cleanup; exact 500-action target state and continued play. Successful final multiplayer runs had zero state divergences, session replacements or reconnects. Steam P2P across separate computers has not been verified.

**Every player must update to v0.1.15 and restart the game.**
