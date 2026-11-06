#!/usr/bin/env python3
# TaoDown 插件 CRX3 打包器（v2.50）。
#
# 背景：v2.47 起本地外链登记用「插件目录 path + version」，Chrome 真机可自动挂载，
# 但 Edge（微软官方文档）只认指向打包好的 .crx 文件的 path（或商店 update_url），
# 目录一律忽略，用户被迫手动挂载。v2.50 改为：随包分发已签名的 taodown.crx，
# 登记键 path 直指该文件，Chrome/Edge/Brave/Vivaldi 等全部自动挂载，零配置零操作。
#
# CRX3 格式与签名算法逐字节对齐 Chromium 官方实现（components/crx_file/crx_creator.cc、
# crx3.proto、crx_file.h，2026-11 核对）：
#   文件 = "Cr24" + LE32(3) + LE32(len(header)) + header + zip
#   header = CrxFileHeader{ sha256_with_rsa:[{public_key: SPKI_DER, signature}],
#                           signed_header_data: bytes }
#   signed_header_data = SignedData{crx_id: SHA256(SPKI)[:16]}
#   签名数据 = b"CRX3 SignedData\x00" + LE32(len(signed_header_data))
#              + signed_header_data + zip 全字节，RSA PKCS#1 SHA-256。
#
# 私钥：tools/extension/taodown-extension.pem（仓库内保管，勿随安装包分发）。
# 扩展 ID 由公钥推导（SHA256(SPKI) 前 16 字节 → a-p 字母表）。
# 首次运行自动生成密钥对并回写 manifest.json 的 key；此后 ID 恒定。
# 若 pem 丢失，ID 必变：需同步 NativeHostRegistrar.ExtensionId、宿主 allowed_origins、
# 测试与 E2E 常量（历史上未备份旧钥，v2.50 即因此换新 ID，旧 ID 保留为兼容来源）。
#
# 用法：python3 tools/extension/pack_crx.py
# 产物：src/extension/taodown.crx（随包分发、登记直指）
import base64
import hashlib
import io
import json
import os
import struct
import sys
import zipfile

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXT_DIR = os.path.join(ROOT, "src", "extension")
PEM_PATH = os.path.join(ROOT, "tools", "extension", "taodown-extension.pem")
CRX_PATH = os.path.join(EXT_DIR, "taodown.crx")

# 与 crx_file.h 一致
SIGNATURE_CONTEXT = b"CRX3 SignedData\x00"
CRX_MAGIC = b"Cr24"


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def proto_bytes(field, value):
    """length-delimited bytes 字段编码。"""
    return varint(field << 3 | 2) + varint(len(value)) + value


def id_from_spki(spki_der):
    # Chromium 规则：SHA256(SPKI) 前 16 字节 → 十六进制字符串 → 0-9a-f 逐一映射为 a-p，共 32 字符
    digest_hex = hashlib.sha256(spki_der).digest()[:16].hex()
    return "".join("abcdefghijklmnop"[int(c, 16)] for c in digest_hex)


def ensure_keypair():
    """读取（或生成）签名私钥，返回 (私钥, SPKI_DER)。"""
    if os.path.exists(PEM_PATH):
        with open(PEM_PATH, "rb") as f:
            key = serialization.load_pem_private_key(f.read(), password=None)
    else:
        key = __import__("cryptography.hazmat.primitives.asymmetric.rsa", fromlist=["rsa"]) \
            .generate_private_key(public_exponent=65537, key_size=2048)
        pem = key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption())
        with open(PEM_PATH, "wb") as f:
            f.write(pem)
        print("已生成新签名私钥: " + os.path.relpath(PEM_PATH, ROOT))
    spki = key.public_key().public_bytes(
        serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    return key, spki


def update_manifest_key(spki_der):
    """把 manifest.json 的 key 换成当前私钥对应的公钥（幂等）。"""
    mpath = os.path.join(EXT_DIR, "manifest.json")
    with open(mpath, "rb") as f:
        raw = f.read()
    text = raw.decode("utf-8-sig")
    manifest = json.loads(text)
    new_key = base64.b64encode(spki_der).decode("ascii")
    if manifest.get("key") == new_key:
        return manifest, False
    manifest["key"] = new_key
    out = json.dumps(manifest, ensure_ascii=False, indent=2)
    with open(mpath, "wb") as f:
        f.write(b"\xef\xbb\xbf" + out.encode("utf-8") + b"\n")
    return manifest, True


def build_zip():
    """按扩展目录内容打 zip（清单、脚本、图标全部进入；排除既有 crx）。"""
    buf = io.BytesIO()
    names = []
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for base, _dirs, files in os.walk(EXT_DIR):
            for name in sorted(files):
                if name.endswith(".crx"):
                    continue
                full = os.path.join(base, name)
                arc = os.path.relpath(full, EXT_DIR).replace(os.sep, "/")
                z.write(full, arc)
                names.append(arc)
    if "manifest.json" not in names:
        raise RuntimeError("manifest.json 缺失")
    return buf.getvalue(), len(names)


def build_crx(key, spki_der, zip_data):
    crx_id = bytes.fromhex("".join("%02x" % b for b in hashlib.sha256(spki_der).digest()[:16]))
    signed_header_data = proto_bytes(1, crx_id)  # SignedData{crx_id}
    digest = hashlib.sha256()
    digest.update(SIGNATURE_CONTEXT)
    digest.update(struct.pack("<I", len(signed_header_data)))
    digest.update(signed_header_data)
    digest.update(zip_data)
    signature = key.sign(
        digest.digest(), padding.PKCS1v15(), hashes.SHA256())

    proof = (proto_bytes(1, spki_der)      # AsymmetricKeyProof.public_key
             + proto_bytes(2, signature))  # AsymmetricKeyProof.signature
    header = (proto_bytes(2, proof)          # CrxFileHeader.sha256_with_rsa（repeated 一条）
              + proto_bytes(10000, signed_header_data))  # CrxFileHeader.signed_header_data
    return (CRX_MAGIC + struct.pack("<I", 3) + struct.pack("<I", len(header))
            + header + zip_data)


def verify_crx(crx_data, expect_id, expect_version):
    """回读自验：魔数/版本/头结构/签名/ID/内含 manifest 版本，全部通过才算数。"""
    from cryptography.hazmat.primitives.serialization import load_der_public_key
    assert crx_data[:4] == CRX_MAGIC, "魔数不符"
    version, = struct.unpack("<I", crx_data[4:8])
    assert version == 3, "版本不符"
    header_len, = struct.unpack("<I", crx_data[8:12])
    header = crx_data[12:12 + header_len]
    zip_data = crx_data[12 + header_len:]

    # 手工解 header proto：field2(repeated proof), field10000(signed_header_data)
    pos, proofs, shd = 0, [], None
    while pos < len(header):
        # tag
        tag, shift = 0, 0
        while True:
            b = header[pos]; pos += 1
            tag |= (b & 0x7F) << shift
            shift += 7
            if not (b & 0x80):
                break
        f, wt = tag >> 3, tag & 7
        assert wt == 2, f"非预期 wiretype {wt}"
        ln, shift = 0, 0
        while True:
            b = header[pos]; pos += 1
            ln |= (b & 0x7F) << shift
            shift += 7
            if not (b & 0x80):
                break
        val = header[pos:pos + ln]; pos += ln
        if f == 2:
            proofs.append(val)
        elif f == 10000:
            shd = val
        else:
            raise AssertionError(f"未知 header 字段 {f}")
    assert proofs and shd is not None, "头结构不完整"

    # 逐条验签（signed_header_data 也在签名覆盖范围内，验签即验 ID）
    for proof in proofs:
        # 解 AsymmetricKeyProof: field1 pk, field2 sig
        p, pk, sig = 0, None, None
        while p < len(proof):
            tag, shift = 0, 0
            while True:
                b = proof[p]; p += 1
                tag |= (b & 0x7F) << shift
                shift += 7
                if not (b & 0x80):
                    break
            f, wt = tag >> 3, tag & 7
            assert wt == 2
            ln, shift = 0, 0
            while True:
                b = proof[p]; p += 1
                ln |= (b & 0x7F) << shift
                shift += 7
                if not (b & 0x80):
                    break
            val = proof[p:p + ln]; p += ln
            if f == 1:
                pk = val
            elif f == 2:
                sig = val
            else:
                raise AssertionError(f"未知 proof 字段 {f}")
        assert pk and sig, "proof 字段缺失"
        shd_len = struct.pack("<I", len(shd))
        digest = hashlib.sha256()
        digest.update(SIGNATURE_CONTEXT)
        digest.update(shd_len)
        digest.update(shd)
        digest.update(zip_data)
        pub = load_der_public_key(pk)
        pub.verify(sig, digest.digest(), padding.PKCS1v15(), hashes.SHA256())
        derived = id_from_spki(pk)
        assert derived == expect_id, f"ID 推导不符: {derived} != {expect_id}"

    # zip 内 manifest 版本核对
    with zipfile.ZipFile(io.BytesIO(zip_data)) as z:
        manifest = json.loads(z.read("manifest.json").decode("utf-8-sig"))
        assert manifest["version"] == expect_version, "zip 内版本不符"
        assert manifest["key"], "zip 内 manifest 缺 key"
    return True


def main():
    key, spki = ensure_keypair()
    ext_id = id_from_spki(spki)
    manifest, key_changed = update_manifest_key(spki)
    zip_data, zip_entries = build_zip()
    crx = build_crx(key, spki, zip_data)
    with open(CRX_PATH, "wb") as f:
        f.write(crx)
    verify_crx(crx, ext_id, manifest["version"])
    print(f"CRX_ID={ext_id}")
    print(f"MANIFEST_VERSION={manifest['version']}")
    print(f"CRX_BYTES={len(crx)} ZIP_ENTRIES={zip_entries}")
    print(f"KEY_CHANGED={'yes' if key_changed else 'no'}")
    print(f"CRX_PATH={os.path.relpath(CRX_PATH, ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
