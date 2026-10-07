#!/usr/bin/env bash
# 演示数据播种：向控制面声明演示账户并派发演示任务，让管理台首屏有真实数据
# （任务走完 创建→派发→执行→终态 全管道，终态含预期的无凭据 failed，见下）。
# PUT 声明幂等覆盖、POST 每次产生一条新任务，部署工作流每次上线后执行一次；
# 本地截图/演示也可复用。
#
# 账户一律 enabled=false：演示实例没有任何真实 Steam 凭据，禁用态让
# reconciler 完全跳过它们（不会产生反复失败的登录任务噪音）。
#
# 用法：BASE_URL=http://127.0.0.1:8085 ADMIN_KEY=<管理员密钥> ./seed-demo.sh
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:8085}"
ADMIN_KEY="${ADMIN_KEY:?ADMIN_KEY required}"
AUTH=(-H "Authorization: Bearer $ADMIN_KEY" -H "Content-Type: application/json")

NAMES=(demo-alpha demo-bravo demo-charlie)
STATES=(Online Idle Farm)
for i in "${!NAMES[@]}"; do
	name="${NAMES[$i]}"
	curl -fsS -X PUT "$BASE_URL/v1/accounts/$name" "${AUTH[@]}" -d "{
		\"enabled\": false,
		\"desiredState\": \"${STATES[$i]}\",
		\"region\": \"demo-west\",
		\"agentId\": \"agent-1\",
		\"note\": \"demo sandbox account (no real credentials)\",
		\"updatedBy\": \"demo-seed\"
	}" > /dev/null
	echo "declared $name (desiredState=${STATES[$i]}, disabled)"
done

# 演示账户没有真实 Steam 凭据，派发的任务会到达 agent、真实执行并以
# 「No credentials provided and no stored session found」落到终态 —— 这正是
# 沙箱该有的样子：完整走通 创建→派发→执行→终态 管道，且绝不触碰 Steam。
dispatch_wave() {
	local wave="$1"
	for name in "${NAMES[@]}"; do
		curl -fsS -X POST "$BASE_URL/v1/jobs" "${AUTH[@]}" \
			-d "{\"action\":\"ping\",\"region\":\"demo-west\",\"targets\":[\"$name\"]}" > /dev/null
		echo "dispatched ping -> $name (wave $wave)"
	done
}

# 验收口径：≥3 个作业到达终态（succeeded|failed）即算管道健康；沙箱无凭据，
# failed(无凭据) 是预期终态。agent 启动早期偶发的隧道中断会把首批任务挂成
# 孤儿（要等 5 分钟租约到期才收尾），所以首轮 45s 未达标就补派新一波 ——
# 新作业秒级到达终态即证明创建→派发→执行→回传全管道健康；孤儿任务的
# 租约清扫由控制面自行完成。三波仍未达标 = 派发管道真断了，判部署失败。
# 计数走 { grep || true; }：set -o pipefail 下 grep 零匹配退出 1 会自伤。
terminal_count() {
	printf '%s' "$1" | { grep -oE '"status":"(succeeded|failed)"' || true; } | wc -l | tr -d ' '
}

body=""
terminal=0
for wave in 1 2 3; do
	dispatch_wave "$wave"
	for attempt in $(seq 1 22); do
		body="$(curl -fsS "$BASE_URL/v1/jobs?page=1&pageSize=50" "${AUTH[@]}" || true)"
		terminal="$(terminal_count "$body")"
		if [ "${terminal:-0}" -ge 3 ]; then
			echo "pipeline verified: $terminal job(s) reached terminal state"
			exit 0
		fi
		sleep 2
	done
	echo "wave $wave: only $terminal terminal job(s) after 45s; retrying" >&2
done
echo "FAIL: jobs did not reach terminal state after $wave waves (last terminal=$terminal)" >&2
exit 1
