#!/bin/bash
# 等 run 45390794895 终态
export DOTNET_ROOT=/root/workspace/.local/dotnet
export PATH=/root/workspace/.local/dotnet:$HOME/.dotnet/tools:/root/workspace/.local/bin:$PATH
RUN=45390794895
while true; do
  S=$(gh run view $RUN --repo T13424343468/TaoDown --json status,conclusion -q '"\(.status) \(.conclusion)"' 2>/dev/null)
  echo "$(date +%H:%M:%S) $S" >> /root/workspace/TaoDown/.assemble.log
  case "$S" in "completed success") break;; "completed failure"|"completed cancelled"|"completed *") echo "$S" >> /root/workspace/TaoDown/.assemble.log; exit 1;; esac
  sleep 30
done
cd /root/workspace/TaoDown
gh run download $RUN --repo T13424343468/TaoDown -n multiview-evidence -D /tmp/evidence-243 2>&1
MSI=$(find /tmp/evidence-243 -name "*.msi" | head -1)
echo "MSI=$MSI" >> /root/workspace/TaoDown/.assemble.log
[ -z "$MSI" ] && exit 2
STAGE=/tmp/taodown-243-pkg
rm -rf $STAGE && mkdir -p $STAGE
rsync -a --exclude .git --exclude .watch.sh --exclude .watch.log --exclude .assemble.sh --exclude .assemble.log ./ $STAGE/ 2>/dev/null || cp -r $(ls -A | grep -v -E '^\.git$|^\.watch|^\.assemble') $STAGE/
cp "$MSI" "$STAGE/TaoDown-2.4.3.0-x64.msi"
cd $STAGE && sha256sum TaoDown-2.4.3.0-x64.msi > SHA256SUMS.txt
cd /tmp && rm -f tao243.zip && zip -qr tao243.zip -C $STAGE . 2>/dev/null || (cd $STAGE && zip -qr /tmp/tao243.zip .)
echo "ZIP=/tmp/tao243.zip" >> /root/workspace/TaoDown/.assemble.log
echo "DONE $(date +%H:%M:%S)" >> /root/workspace/TaoDown/.assemble.log
