#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
test_actions.py — PC 端动作的端到端自测。

不发真实动作（默认 dry-run），而是模拟手机推送 presence 事件，
验证「事件 → 计时 → 触发动作」这条链路是通的。

用法：
    python pc/test_actions.py                    # dry-run 测试
    python pc/test_actions.py --real             # 真执行（会真的锁屏！慎用）
    python pc/test_actions.py --action lock      # 指定要测的动作
"""

import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sentinel_receiver import Actions, EventHandler  # noqa: E402


def send(handler: EventHandler, present: bool) -> None:
    """模拟手机端推送一条 presence 事件。"""
    handler.on_event({
        "event": "presence",
        "present": present,
        "confidence": 0.87,
        "ts": time.time() * 1000,
    })


def main() -> int:
    ap = argparse.ArgumentParser(description="PC 端动作自测")
    ap.add_argument("--action", action="append", default=None,
                    help="要测的动作，可重复。默认 notify")
    ap.add_argument("--absent-seconds", type=float, default=1.0)
    ap.add_argument("--present-seconds", type=float, default=1.0)
    ap.add_argument("--trigger", choices=["absent", "present", "both"], default="absent")
    ap.add_argument("--real", action="store_true",
                    help="真执行动作（--real 会真的锁屏！）")
    args = ap.parse_args()

    names = args.action or ["notify"]
    dry = not args.real

    print("=" * 62)
    print("  工位哨兵 · PC 端动作自测")
    print(f"  动作: {names}   触发: {args.trigger}   "
          f"{'DRY-RUN' if dry else '★ 真执行 ★'}")
    print("=" * 62)

    actions = Actions(dry_run=dry, cooldown=0.5)
    handler = EventHandler(actions,
                           trigger=args.trigger,
                           absent_seconds=args.absent_seconds,
                           present_seconds=args.present_seconds)
    handler.action_names = names

    # --- 场景 1：人一直都在 → 不应触发 ---
    print("\n【场景1】人一直在（连发 3 次有人）")
    for _ in range(3):
        send(handler, True)
        time.sleep(0.2)
    print("  -> 期望：不触发任何动作")

    # --- 场景 2：人离开但时间不够 → 不应触发 ---
    print(f"\n【场景2】人离开，但只持续 {args.absent_seconds * 0.5}s（不到阈值）")
    send(handler, False)
    time.sleep(args.absent_seconds * 0.5)
    send(handler, True)   # 人回来了，重置计时
    print("  -> 期望：不触发（时间不够）")

    # --- 场景 3：人离开超过阈值 → 应触发 ---
    # ★ 注意：PC 端的计时是【事件驱动】的——
    #   阈值检查发生在收到 presence 事件的时刻。
    #   手机端每秒发一次快照，所以实际最多延迟 1 秒。
    #   这里也模拟成「先离开，等阈值过了，再发一条推进检查」。
    print(f"\n【场景3】人离开超过 {args.absent_seconds}s")
    send(handler, False)
    time.sleep(args.absent_seconds + 0.3)
    send(handler, False)      # 推进检查
    print("  -> 期望：触发动作")

    # --- 场景 4：持续无人，不应重复触发 ---
    print("\n【场景4】持续无人，再发 3 次（模拟手机端每秒发快照）")
    for _ in range(3):
        send(handler, False)
        time.sleep(0.4)
    print("  -> 期望：不重复触发（一次离开只触发一次）")

    # --- 场景 5：人回来 → 重置，之后再离开应能再次触发 ---
    print("\n【场景5】人回来（重置触发状态），再离开")
    send(handler, True)
    time.sleep(0.5)
    send(handler, False)
    time.sleep(args.absent_seconds + 0.3)
    send(handler, False)      # 推进检查
    print("  -> 期望：再次触发")

    # --- 场景 6：present 模式下人回来应触发 ---
    if args.trigger in ("present", "both"):
        print(f"\n【场景6】人回来超过 {args.present_seconds}s")
        send(handler, True)
        time.sleep(args.present_seconds + 0.3)
        print("  -> 期望：触发动作")

    print("\n" + "=" * 62)
    print("  自测结束")
    print("=" * 62)
    return 0


if __name__ == "__main__":
    sys.exit(main())
