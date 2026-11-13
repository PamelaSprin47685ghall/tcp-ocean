# Review group 1 (correctness): grant, credit pool, JOIN admission

## (a) Grant from each report's own ack (5f989ac)
`TxHalf.OnPeerAck`: `limit = max(limit, ack + grant)` with the report's own `ack` (after `ack <= head`),
was `peerAck + grant`: a late report with an older ack and a larger relative grant was applied to the
newer peerAck. Test: scripted reports (late, duplicate, invalid ack > head) and 2000 random
interleavings against the model max(ack + grant). On the old code: expected 4246, got 4248.

## (b) Session credit pool (4dbc435, DECISIONS D8)
Commitment of a flow = `RxLimit - consumed` (reserve bytes held + credit not arrived); after FIN/RST
only the reserve. C0 of all 256 slots is inside the 32 MiB pool (reserved up front: the peer may open
any slot with C0 at any time); the other 16 MiB is shared, charged before a limit grows, released only
by consumption or close. `FlowTable.CommittedBytes` <= PoolBytes always. Test: one flow fills 8 MiB
(held), then 255 flows open one by one with readers that take nothing: committed <= 32 MiB, every
flow >= C0, no limit ever falls; the old split committed 41.7 MiB in this test (~161 MiB worst case).
Cost: with 3+ open receive flows the per-flow window is smaller (4 flows: ~4.06 MiB vs 8 MiB).

## (c) JOIN admission (628734c)
`JoinAdmission` (one per server session, owned by the server loop): per path id `highest_seen` and
`active` incarnation; decisions keyed by JTH and join_nonce; each JOIN_ACK rendered once and kept
frozen (30 s, a proxy for its TCP delivery), a repeat gets the identical bytes and never activates a
connection; same nonce with another transcript, old or equal incarnation: close, no ACK. Refusals
(CAPACITY, INVALID_REQUEST, SESSION_CLOSING) consume the incarnation and keep the active one. Bounds:
1024 digests, 288 frozen ACKs; full -> refuse the new admission with no state change, never evict a
record that still holds a frozen ACK; old incarnations stay closed after digests age out (per-path
highest). Accept of a live path id replaces the old path (`SessionCarrier.RemovePath`). Wire format
and vectors unchanged. netns sanity (0 %, ddc9163): 4 paths joined, bulk up 143.4 / down 143.3 Mbit/s.
