# 生成 TaoDown 软件大图标（v2.49 统一版）：
# 与浏览器插件小图标（src/extension/icons/icon128.png）完全同款同源：
# 太极圆盘——上半宣纸浅 #EEF0EB、下半浓墨 #1C1E22，上下各一枚反向鱼眼，
# 浓墨半内嵌宣纸色下载箭头。桌面 exe/窗口/托盘/安装包全部统一用这一式。
# 几何参数按 icon128 实测比例 ×2 落到 256 画布（128 基准：圆心 63.5,63.5 R=56.5；
# 鱼眼 r=8.5 圆心 ±28.5；箭头轴 x=98.5 柄 9x12 头宽 22 高 17 尖 y=97）。
import os
from PIL import Image, ImageDraw

S = 4                      # 超采样倍数（1024 画布）
N = 256 * S
IMG = Image.new("RGBA", (N, N), (0, 0, 0, 0))
D = ImageDraw.Draw(IMG)

CREAM = (238, 240, 235, 255)   # 宣纸浅（与插件一致 #EEF0EB）
INK = (28, 30, 34, 255)        # 浓墨（与插件一致 #1C1E22）

def px(v):  # 128 逻辑像素 → 超采样画布（×2 到 256，再 ×S）
    return int(v * 2 * S)

CX = px(63.5)
CY = px(63.5)
R = px(56.5)
EYE_R = px(8.5)

# ---- 太极圆盘: 上半宣纸浅 / 下半浓墨（水平分界过圆心）----
D.pieslice([CX - R, CY - R, CX + R, CY + R], 180, 360, fill=CREAM)  # 上半(浅)
D.pieslice([CX - R, CY - R, CX + R, CY + R], 0, 180, fill=INK)      # 下半(墨)

# ---- 两枚反向鱼眼: 上浅半嵌浓墨眼, 下浓墨半嵌宣纸眼 ----
EYE_OFF = px(28.5)
D.ellipse([CX - EYE_R, CY - EYE_OFF - EYE_R, CX + EYE_R, CY - EYE_OFF + EYE_R], fill=INK)
D.ellipse([CX - EYE_R, CY + EYE_OFF - EYE_R, CX + EYE_R, CY + EYE_OFF + EYE_R], fill=CREAM)

# ---- 下载箭头（浓墨半内, 宣纸色, 与插件同位同形）----
AX = px(98.5)
SH_W = px(4.5)                 # 柄半宽(9/2)
SH_TOP, SH_BOT = px(68), px(79)
HEAD_TOP, APEX = px(80), px(97)
HEAD_HALF = px(10.5)
D.rectangle([AX - SH_W, SH_TOP, AX + SH_W, SH_BOT], fill=CREAM)
D.polygon([(AX - HEAD_HALF, HEAD_TOP), (AX + HEAD_HALF, HEAD_TOP), (AX, APEX)], fill=CREAM)

# ---- 输出: 抗锯齿缩回 + 多尺寸 ico/png ----
final = IMG.resize((256, 256), Image.LANCZOS).convert("RGBA")
os.makedirs("src/TaoDown.App/Assets", exist_ok=True)
final.save("src/TaoDown.App/Assets/taodown.ico", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
final.save("tools/icon/taodown_v249_preview.png")
print("icon ok: taiji disc, cream top / ink bottom, arrow in ink half")
