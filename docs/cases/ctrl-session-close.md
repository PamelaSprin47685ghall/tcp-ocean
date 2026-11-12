# CTRL 可靠控制与 SESSION_CLOSE 接入(spec 6.10a)

合入:cb15e3c(分支 ctrl-wire 7c915d6、79f7e50)。

- WIN_ACK 携带 ctrl_ack_hi / ctrl_ack_bitmap;CTRL 优先于数据发送;未确认的项每 1.05×SRTT 重发。
- 收到重复项只重发确认;不支持、未知、窗外的操作不执行、不确认(计数 ctrl_rejected)。
- 关闭状态:Open → Closing(我方发 SESSION_CLOSE)/ Draining(对方发来,我方 SESSION_CLOSED 待确认)→ Closed。
  超过重试期限:关闭中的会话直接 Closed;Open 的会话失败("control retry limit")。
- 客户端收到 SIGINT/SIGTERM 时发 SESSION_CLOSE,最多等 2 s;服务端日志 "closed by peer (reason N)"。
- 证据:4 个会话测试(30% 丢包下完成关闭、重复只执行一次、拒绝类、重试期限),共 136 个测试通过;
  e2e run.sh 的 stop 先给客户端发 SIGTERM,并要求服务端记录对端关闭:5%、0% 两个会话都是 "session close: ok"。
