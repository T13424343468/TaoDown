#!/bin/bash
RUN=45361560341
for i in $(seq 1 400); do
  S=$(gh run view $RUN --repo T13424343468/TaoDown --json status,conclusion -q '"\(.status) \(.conclusion)"' 2>/dev/null)
  echo "$(date +%H:%M:%S) $S" >> /root/workspace/TaoDown/.watch.log
  case "$S" in completed*) break;; esac
  sleep 30
done
