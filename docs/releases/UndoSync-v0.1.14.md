# multiplayer-undo v0.1.14

- Acknowledge and retry undo control messages, including votes, replay completion and cancellation. Ignore duplicate deliveries and preserve history chunk order.
- Report each peer's replay stage, outstanding acknowledgements and missing completion receipts in PID logs. Preserve and relay the first failure reason.
- Handle individual client disconnections during undo instead of waiting for an absent participant's completion.
- Cancel discarded combat presentation waits before rebuilding run owners, preventing the reproduced old-card continuation error.
- Accelerate restoration/replay with native Instant mode and restore the player's original speed afterward.
- Retain the multiplayer room and compare every player's restored checksum before resuming.

All players must update to v0.1.14 and restart the game. The control protocol changed; mixed older versions are incompatible.

Validation: real local ENet sessions with 3 and 4 game processes; 14 message/ACK loss cases in one room, repeated undo, continued play, disconnection, delayed old action and failure propagation. Both Normal and Instant settings were checked. Authenticated Steam multiplayer across separate computers has not been verified.
