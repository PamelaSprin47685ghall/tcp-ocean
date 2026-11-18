# mihomo 看门狗

mihomo 的 tbt4 vless 入站会在大流量持续约 30 分钟后停止读取 tbtun 送来的数据：
进程还活着、18443 还在监听，但 `Recv-Q` 涨到 MB 级，tbtun 侧 `Send-Q` 同步积压，
控制面仍可能回 204，大流量却归零。`Restart=always` 不会触发，因为进程没有退出。

看门狗每 60 秒读一次 `ss -tn`：

- `local=127.0.0.1:18443` 且 `ESTAB`：累加 `Recv-Q`（mihomo 未读）
- `peer=127.0.0.1:18443` 且 `ESTAB`：累加 `Send-Q`（tbtun 未发出）

任一超过 `MIHOMO_WATCHDOG_BYTES`（默认 262144，即 256 KiB）就 `systemctl restart`
对应的 mihomo 单元，并写 syslog（`logger -t mihomo-watchdog`）。两次重启至少间隔 120 秒。

## 文件

| 文件 | 用途 |
|---|---|
| `mihomo-watchdog.sh` | 检查并重启脚本 |
| `mihomo-watchdog@.service` | 模板 oneshot，`%i` 是 mihomo 单元名 |
| `mihomo-watchdog@.timer` | 模板 timer，开机 90 秒后首次，此后每 60 秒 |

## 本地（client 侧的 mihomo-hnd0tbt4）

```sh
sudo install -d /usr/local/libexec
sudo install -m755 deploy/mihomo-watchdog.sh /usr/local/libexec/mihomo-watchdog.sh
sudo install -m644 deploy/mihomo-watchdog@.service /etc/systemd/system/
sudo install -m644 deploy/mihomo-watchdog@.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now mihomo-watchdog@mihomo-hnd0tbt4.timer
systemctl list-timers 'mihomo-watchdog*' --no-pager
```

## HND0（server 侧的 mihomo-tbt4）

```sh
ssh root@45.11.1.62 'install -d /usr/local/libexec'
ssh root@45.11.1.62 'cat > /usr/local/libexec/mihomo-watchdog.sh && chmod 755 /usr/local/libexec/mihomo-watchdog.sh' < deploy/mihomo-watchdog.sh
ssh root@45.11.1.62 'cat > /etc/systemd/system/mihomo-watchdog@.service' < deploy/mihomo-watchdog@.service
ssh root@45.11.1.62 'cat > /etc/systemd/system/mihomo-watchdog@.timer' < deploy/mihomo-watchdog@.timer
ssh root@45.11.1.62 'systemctl daemon-reload && systemctl enable --now mihomo-watchdog@mihomo-tbt4.timer'
ssh root@45.11.1.62 "systemctl list-timers 'mihomo-watchdog*' --no-pager"
```

## 手动验证

```sh
# 立即跑一次检查（有积压时会重启 mihomo）
sudo systemctl start mihomo-watchdog@mihomo-hnd0tbt4.service
ssh root@45.11.1.62 'systemctl start mihomo-watchdog@mihomo-tbt4.service'

# 重启后积压应清空
ss -tn | grep 18443
ssh root@45.11.1.62 'ss -tn | grep 18443'

# 看门狗日志
journalctl -t mihomo-watchdog --no-pager | tail
ssh root@45.11.1.62 'journalctl -t mihomo-watchdog --no-pager | tail'
```

## 阈值调整

环境变量 `MIHOMO_WATCHDOG_BYTES` 可覆盖默认值。改 `/etc/systemd/system/mihomo-watchdog@.service`：

```ini
Environment=MIHOMO_WATCHDOG_BYTES=524288
```

然后 `systemctl daemon-reload`。

## 注意

看门狗只是自动恢复，不是根本修复。根本修复在 mihomo 侧：限制单连接缓冲、升级版本，
或给 vless 入站加读超时。在这些完成前，不要用“控制面 204”判断链路可用。
