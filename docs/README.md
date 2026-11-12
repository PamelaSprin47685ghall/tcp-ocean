# 文档

- `spec/TBTun-主文档-v2.2.md`：协议规范 v2.2，唯一权威来源。代码注释中的“spec x.y”指本文件章节。
- `DECISIONS.md`：规范未写死、本实现定下的互通规则。
- 测试向量在 `../tests/vectors/`：
  - `tbt-sc2-vectors.json`、`join-ack-v22-vectors.json`：随规范发布的规范性向量。
  - `handshake-vectors.json`：由 `make_handshake_vectors.py`（Python `cryptography`，与 F# 代码无共享）按 spec 6.3–6.5 独立生成。
