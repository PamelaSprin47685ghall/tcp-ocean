# 本地代理看门狗

本地代理的 tbt4 vless 入站会在大流量持续约 30 分钟后停止读取 tbtun 送来的数据：
进程还活着、18443 还在监听，但 `Recv-Q` 涨到 MB 级，tbtun 侧 `Send-Q` 同步积压，
控制面仍可能回 204，大流量却归零。`Restart=always` 不会触发，因为进程没有退出。

看门狗每 60 秒读一次 `ss -tn`：

- `local=127.0.0.1:18443` 且 `ESTAB`：累加 `Recv-Q`（本地代理未读）
- `peer=127.0.0.1:18443` 且 `ESTAB`：累加 `Send-Q`（tbtun 未发出）

任一超过 `PROXY_WATCHDOG_BYTES`（默认 4194304，即 4 MiB）就 `systemctl restart`
对应的本地代理单元，并写 syslog（`logger -t proxy-watchdog`）。两次重启至少间隔 120 秒。

## 文件

| 文件 | 用途 |
|---|---|
| `proxy-watchdog.sh` | 检查并重启脚本 |
| `proxy-watchdog@.service` | 模板 oneshot，`%i` 是本地代理单元名 |
| `proxy-watchdog@.timer` | 模板 timer，开机 90 秒后首次，此后每 60 秒 |

## 本地（client 侧的 proxy-hnd0tbt4）

```sh
sudo install -d /usr/local/libexec
sudo install -m755 deploy/proxy-watchdog.sh /usr/local/libexec/proxy-watchdog.sh
sudo install -m644 deploy/proxy-watchdog@.service /etc/systemd/system/
sudo install -m644 deploy/proxy-watchdog@.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now proxy-watchdog@proxy-hnd0tbt4.timer
systemctl list-timers 'proxy-watchdog*' --no-pager
```

## HND0（server 侧的本地代理-tbt4）

```sh
ssh root@45.11.1.62 'install -d /usr/local/libexec'
ssh root@45.11.1.62 'cat > /usr/local/libexec/proxy-watchdog.sh && chmod 755 /usr/local/libexec/proxy-watchdog.sh' < deploy/proxy-watchdog.sh
ssh root@45.11.1.62 'cat > /etc/systemd/system/proxy-watchdog@.service' < deploy/proxy-watchdog@.service
ssh root@45.11.1.62 'cat > /etc/systemd/system/proxy-watchdog@.timer' < deploy/proxy-watchdog@.timer
ssh root@45.11.1.62 'systemctl daemon-reload && systemctl enable --now proxy-watchdog@本地代理-tbt4.timer'
ssh root@45.11.1.62 "systemctl list-timers 'proxy-watchdog*' --no-pager"
```

## 手动验证

```sh
# 立即跑一次检查（有积压时会重启本地代理）
sudo systemctl start proxy-watchdog@proxy-hnd0tbt4.service
ssh root@45.11.1.62 'systemctl start proxy-watchdog@本地代理-tbt4.service'

# 重启后积压应清空
ss -tn | grep 18443
ssh root@45.11.1.62 'ss -tn | grep 18443'

# 看门狗日志
journalctl -t proxy-watchdog --no-pager | tail
ssh root@45.11.1.62 'journalctl -t proxy-watchdog --no-pager | tail'
```

## 阈值调整

环境变量 `PROXY_WATCHDOG_BYTES` 可覆盖默认值。默认 4 MiB 是有依据的下限：
一次 30 MiB 的正常上传会让 18443 的队列自然积压到 6–11 MB，阈值低于这个量级就会
把健康传输误判成卡死（256 KiB 曾造成这种误判，重启又把链路推进高 RTT 窗口）。
真卡死时观测到的积压是 3–20 MB。改 `/etc/systemd/system/proxy-watchdog@.service`：

```ini
Environment=PROXY_WATCHDOG_BYTES=8388608
```

然后 `systemctl daemon-reload`。

## 注意

看门狗只是自动恢复，不是根本修复。根本修复在本地代理侧：限制单连接缓冲、升级版本，
或给 vless 入站加读超时。在这些完成前，不要用“控制面 204”判断链路可用。
阈值不要为了“更灵敏”而调小：正常大流量上传的队列深度会高于任何小阈值。
