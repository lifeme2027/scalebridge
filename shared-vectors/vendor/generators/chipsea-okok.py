#!/usr/bin/env python3
"""Generates shared-vectors/vendor/chipsea-okok.json.

Frames are hand-built from the layout documented in NOTES.md (NOT captured from devices).
Expected values are computed independently of the C# parser using decimal.Decimal.
Run: python3 shared-vectors/vendor/generators/chipsea-okok.py > shared-vectors/vendor/chipsea-okok.json
"""
import json
from decimal import Decimal as D
from functools import reduce

LB_TO_KG = D("0.45359237")  # defined value
JIN_TO_KG = D("0.5")        # 1 jin = 500 g
LB_PER_ST = 14              # 1 stone = 14 lb

def xor(bs):
    return reduce(lambda a, b: a ^ b, bs, 0)

def hexs(b):
    return " ".join(f"{x:02X}" for x in b)

def dec_str(x):
    return format(x.normalize(), "f")

AD = "advertisement-manufacturer-data"
F11, F20, FD = "chipsea-okok-v11-broadcast-19", "chipsea-okok-v20-broadcast-15", "chipsea-okok-manufacturer-data-dispatch"
MAC = bytes([0xC8, 0xB2, 0x1E, 0x15, 0x6E, 0xDB])

def vec(name, frame, data, desc, expect=None, warnings=(), code=None, context=None):
    v = {"name": name, "description": desc, "service": None, "characteristic": None,
         "source": AD, "frameType": frame, "hex": hexs(data)}
    if context: v["context"] = context
    if code:
        v["reject"] = True; v["errorCode"] = code
    else:
        v["reject"] = False; v["warnings"] = list(warnings); v["expect"] = expect
    return v

# ---------------- v1.1 (0xCA 0x11, 19 bytes) ----------------
def build11(lock, dev, raw, prod, props, rsv=bytes(6), cs="spec", tail=b"", ver=0x11, ln=0x0F, magic=0xCA):
    body = bytes([lock, dev, raw >> 8, raw & 0xFF]) + prod + bytes([props]) + rsv  # data domain, 15 bytes
    head = bytes([magic, ver, ln])
    spec = xor(bytes([ver, ln]) + body)          # documented rule: XOR from version byte
    obs = xor(bytes([magic, ver, ln]) + body)    # rule seen in one public capture: XOR including 0xCA
    c = {"spec": spec, "start": obs, "bad": (spec ^ 0x55) if (spec ^ 0x55) != obs else (spec ^ 0x66)}[cs]
    return head + body + bytes([c]) + tail

def w11(raw, props):
    unit = (props >> 3) & 3
    dec = (props >> 1) & 3
    if unit == 3:  # st:lb
        lb = D(raw >> 8) * LB_PER_ST + D(raw & 0xFF) / 10
        return "StoneLb", dec_str(lb), dec_str(lb * LB_TO_KG)
    digits = {0: 1, 1: 0, 2: 2}[dec]
    orig = D(raw) / (D(10) ** digits)
    if unit == 0: return "Kg", dec_str(orig), dec_str(orig)
    if unit == 1: return "Jin", dec_str(orig), dec_str(orig * JIN_TO_KG)
    return "Lb", dec_str(orig), dec_str(orig * LB_TO_KG)

def ok11(name, desc, lock=0, raw=731, props=0x00, cs="spec", tail=b"", dev=0x01, prod=bytes([0x11, 0x22, 0x33, 0x44]), rsv=bytes(6), warnings=()):
    unit, orig, kg = w11(raw, props)
    data = build11(lock, dev, raw, prod, props, rsv, cs, tail)
    return vec(name, F11, data, desc, warnings=warnings, expect={
        "protocol": "ChipseaOkok11", "rawWeight": raw, "rawUnit": unit,
        "originalWeight": orig, "weightKg": kg, "timestamp": None, "impedanceRaw": None,
        "stabilized": lock == 1, "weightRemoved": False, "isFinal": lock == 1})

def rej11(name, desc, data, code, frame=F11, context=None):
    return vec(name, frame, data, desc, code=code, context=context)

# ---------------- v2.0 (0xCA 0x20, 15 bytes) ----------------
def build20(final, raw, imp, cs="spec", unk=bytes(4), prop=0x01, seq=0x31, tail=b"", ver=0x20, ln=0x0B, magic=0xCA):
    body = unk + bytes([prop, final, seq, raw >> 8, raw & 0xFF, imp >> 8, imp & 0xFF])  # 11 bytes
    spec = xor(bytes([ver, ln]) + body)
    c = spec if cs == "spec" else spec ^ 0x5A
    return bytes([magic, ver, ln]) + body + bytes([c]) + tail

def ok20(name, desc, final=1, raw=812, imp=4871, unit=None, tail=b"", cs="spec", unk=bytes(4), prop=0x01, seq=0x31):
    u = unit or "Kg"
    orig = D(raw) / 10
    kg = {"Kg": orig, "Lb": orig * LB_TO_KG, "Jin": orig * JIN_TO_KG}[u]
    data = build20(final, raw, imp, cs, unk, prop, seq, tail)
    ctx = {"assumedUnit": unit} if unit else None
    return vec(name, F20, data, desc, context=ctx, warnings=([] if unit else ["UnitAssumed"]), expect={
        "protocol": "ChipseaOkok20", "rawWeight": raw, "rawUnit": u,
        "originalWeight": dec_str(orig), "weightKg": dec_str(kg), "timestamp": None, "impedanceRaw": imp,
        "stabilized": final == 1, "weightRemoved": False, "isFinal": final == 1})

good11 = build11(1, 1, 731, bytes([0x11, 0x22, 0x33, 0x44]), 0x00)
good20 = build20(1, 812, 4871)

vectors = [
    # --- v1.1 units / decimals (property byte = Table 1 of the vendor document) ---
    ok11("chipsea11-kg-1dec-unlocked", "73.1 kg: raw 731, props 0x00, not locked", lock=0, raw=731, props=0x00),
    ok11("chipsea11-kg-1dec-locked", "73.1 kg locked -> stabilized/isFinal", lock=1, raw=731, props=0x00),
    ok11("chipsea11-vendor-example-50kg", "vendor doc example: 50 kg, 1 decimal, raw 0x01F4", lock=0, raw=0x01F4, props=0x00),
    ok11("chipsea11-vendor-example-50lb", "vendor doc example: 50 LB, 1 decimal, props 0x10, raw 0x01F4", lock=0, raw=0x01F4, props=0x10),
    ok11("chipsea11-stlb-4st-13lb", "4 st 13.0 lb: raw 0x0482, props 0x18 (unit bits 11)", lock=1, raw=0x0482, props=0x18),
    ok11("chipsea11-stlb-ignores-decimal-bits", "st:lb with decimal bits set (0x1A) -> same result", lock=1, raw=0x0482, props=0x1A),
    ok11("chipsea11-stlb-lb-only", "0 st 9.5 lb: raw 0x005F", lock=0, raw=0x005F, props=0x18),
    ok11("chipsea11-kg-0dec", "75 kg, 0 decimals (bits 2-1 = 01)", lock=1, raw=75, props=0x02),
    ok11("chipsea11-kg-2dec", "73.10 kg, 2 decimals (bits 2-1 = 10)", lock=1, raw=7310, props=0x04),
    ok11("chipsea11-kg-2dec-odd", "123.45 kg, 2 decimals", lock=1, raw=12345, props=0x04),
    ok11("chipsea11-jin-1dec", "150.0 jin = 75 kg: props 0x08", lock=1, raw=1500, props=0x08),
    ok11("chipsea11-jin-2dec", "150.25 jin: props 0x0C", lock=1, raw=15025, props=0x0C),
    ok11("chipsea11-lb-0dec", "150 lb: props 0x12", lock=1, raw=150, props=0x12),
    ok11("chipsea11-lb-1dec", "165.3 lb: props 0x10", lock=1, raw=1653, props=0x10),
    ok11("chipsea11-lb-2dec", "150.25 lb: props 0x14", lock=1, raw=15025, props=0x14),
    ok11("chipsea11-zero", "zero weight is still a valid frame", lock=0, raw=0, props=0x00),
    ok11("chipsea11-max-raw-kg", "raw 65535, 1 decimal = 6553.5 kg", lock=1, raw=0xFFFF, props=0x00),
    ok11("chipsea11-reserved-prop-bits-ignored", "props 0xE1: reserved bits 7-5 and 0 set, kg 1 decimal", lock=1, raw=731, props=0xE1),
    ok11("chipsea11-reserved-bytes-and-devtype-ignored", "nonzero hold-back bytes and device type 0x02 are ignored", lock=1, raw=731, props=0x00, dev=0x02, rsv=bytes([1, 2, 3, 4, 5, 6])),
    ok11("chipsea11-lock-byte-2-not-final", "lock byte 2 is neither documented value: treated as not locked", lock=2, raw=731, props=0x00),
    ok11("chipsea11-checksum-including-start-byte", "checksum computed with 0xCA included (rule seen in one public capture) is accepted", lock=1, raw=731, props=0x00, cs="start"),
    ok11("chipsea11-trailing-mac-ignored", "6 bytes after the checksum (device address in real advertisements) are ignored, no warning", lock=1, raw=731, props=0x00, tail=MAC),
    # --- v1.1 rejects ---
    rej11("chipsea11-empty", "empty input", b"", "TooShort"),
    rej11("chipsea11-too-short-18", "18 bytes", good11[:18], "TooShort"),
    rej11("chipsea11-bad-magic", "first byte 0xCB", build11(1, 1, 731, bytes(4), 0, magic=0xCB), "BadHeader"),
    rej11("chipsea11-bad-version", "version byte 0x12", build11(1, 1, 731, bytes(4), 0, ver=0x12), "BadHeader"),
    rej11("chipsea11-bad-length-field", "data-domain length 0x0E instead of 0x0F", build11(1, 1, 731, bytes(4), 0, ln=0x0E), "BadHeader"),
    rej11("chipsea11-bad-checksum", "checksum matches neither accepted rule", build11(1, 1, 731, bytes([0x11, 0x22, 0x33, 0x44]), 0, cs="bad"), "BadChecksum"),
    rej11("chipsea11-bad-checksum-flipped-weight", "weight byte changed after checksum", good11[:6] + bytes([good11[6] ^ 0x01]) + good11[7:], "BadChecksum"),
    rej11("chipsea11-undefined-decimal-bits", "decimal bits 11 are not defined in the document (props 0x06)", build11(1, 1, 731, bytes(4), 0x06), "InvalidValue"),
    # --- v2.0 ---
    ok20("chipsea20-final-kg-assumed", "81.2 kg final, impedance raw 0x1307 = 4871; unit not in frame -> kg assumed + UnitAssumed warning", final=1, raw=812, imp=4871),
    ok20("chipsea20-unfinal", "still stabilizing: final flag 0", final=0, raw=812, imp=4871),
    ok20("chipsea20-lb-context", "caller says the scale shows lb: 179.0 lb, no UnitAssumed warning", final=1, raw=1790, imp=4871, unit="Lb"),
    ok20("chipsea20-jin-context", "caller says jin: 162.4 jin = 81.2 kg", final=1, raw=1624, imp=4871, unit="Jin"),
    ok20("chipsea20-kg-context-explicit", "explicit kg context: no warning", final=1, raw=812, imp=4871, unit="Kg"),
    ok20("chipsea20-zero-weight", "zero weight idle frame", final=0, raw=0, imp=0),
    ok20("chipsea20-max-raw", "raw 65535 / 10 = 6553.5", final=1, raw=0xFFFF, imp=0xFFFF),
    ok20("chipsea20-unknown-bytes-ignored", "nonzero unknown bytes 3-6/7 and sequence are ignored", final=1, raw=812, imp=4871, unk=bytes([9, 8, 7, 6]), prop=0x03, seq=0x10),
    ok20("chipsea20-trailing-mac-ignored", "6-byte device address after the checksum is ignored", final=1, raw=812, imp=4871, tail=MAC),
    rej11("chipsea20-empty", "empty input", b"", "TooShort", frame=F20),
    rej11("chipsea20-too-short-14", "14 bytes", good20[:14], "TooShort", frame=F20),
    rej11("chipsea20-bad-magic", "first byte 0xC0", build20(1, 812, 4871, magic=0xC0), "BadHeader", frame=F20),
    rej11("chipsea20-bad-version", "version byte 0x21", build20(1, 812, 4871, ver=0x21), "BadHeader", frame=F20),
    rej11("chipsea20-bad-length-field", "length byte 0x0A instead of 0x0B", build20(1, 812, 4871, ln=0x0A), "BadHeader", frame=F20),
    rej11("chipsea20-bad-checksum", "wrong checksum", build20(1, 812, 4871, cs="bad"), "BadChecksum", frame=F20),
    rej11("chipsea20-stonelb-context-unsupported", "stone:pound is not defined for this frame", good20, "InvalidValue", frame=F20, context={"assumedUnit": "StoneLb"}),
    # --- dispatcher ---
    vec("chipsea-dispatch-v11", FD, good11, "byte1 0x11 -> v1.1 parser",
        expect=ok11("x", "x", lock=1, raw=731, props=0x00)["expect"], warnings=[]),
    vec("chipsea-dispatch-v20", FD, good20, "byte1 0x20 -> v2.0 parser",
        expect=ok20("x", "x", final=1, raw=812, imp=4871)["expect"], warnings=["UnitAssumed"]),
    vec("chipsea-dispatch-v20-lb-context", FD, build20(1, 1790, 4871), "context passes through to v2.0",
        expect=ok20("x", "x", final=1, raw=1790, imp=4871, unit="Lb")["expect"], warnings=[], context={"assumedUnit": "Lb"}),
    rej11("chipsea-dispatch-unknown-version", "byte1 0x30 is not a known frame version", bytes([0xCA, 0x30, 0x0B]) + bytes(12), "BadHeader", frame=FD),
    rej11("chipsea-dispatch-bad-magic", "first byte not 0xCA", bytes([0xC0, 0x20, 0x0B]) + bytes(12), "BadHeader", frame=FD),
    rej11("chipsea-dispatch-one-byte", "one byte", bytes([0xCA]), "TooShort", frame=FD),
    rej11("chipsea-dispatch-empty", "empty", b"", "TooShort", frame=FD),
]

# sanity: names unique
assert len({v["name"] for v in vectors}) == len(vectors)

doc = {
    "format": "scalebridge-vectors/1",
    "suite": "vendor-chipsea-okok",
    "vendor": "chipsea-okok",
    "description": "Chipsea/OKOK advertisement frames: v1.1 (0xCA 0x11, 19 bytes, vendor document) and v2.0 (0xCA 0x20, 15 bytes, public prose). Hand-built from the layout in NOTES.md; EXPERIMENTAL, not verified on real hardware. See shared-vectors/vendor/VENDOR.md.",
    "experimental": True,
    "vectors": vectors,
}
print(json.dumps(doc, indent=2, ensure_ascii=False))
