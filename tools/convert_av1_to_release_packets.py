#!/usr/bin/env python
import argparse
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
from pathlib import Path


RELEASE_PACKET_HEADER_FORMAT = "<8i"
RELEASE_PACKET_HEADER_SIZE = struct.calcsize(RELEASE_PACKET_HEADER_FORMAT)
DEFAULT_MAX_FRAGMENT_PAYLOAD = 1500 - RELEASE_PACKET_HEADER_SIZE
HEX_CHUNK_RE = re.compile(r"[0-9A-Fa-f]+")


def resolve_tool(explicit_path: str | None, env_name: str, default_windows_path: str, fallback_name: str) -> str:
    if explicit_path:
        return explicit_path
    env_value = os.environ.get(env_name)
    if env_value:
        return env_value
    fallback = shutil.which(fallback_name)
    if fallback:
        return fallback
    if os.path.exists(default_windows_path):
        return default_windows_path
    raise FileNotFoundError(f"Could not find {fallback_name}. Set --{env_name.lower().replace('_', '-')} or {env_name}.")


def run_checked(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, check=True, text=True, capture_output=True)


def extract_obu_stream(ffmpeg_path: str, input_path: Path, output_obu_path: Path) -> None:
    command = [
        ffmpeg_path,
        "-y",
        "-i",
        str(input_path),
        "-map",
        "0:v:0",
        "-c",
        "copy",
        "-f",
        "obu",
        str(output_obu_path),
    ]
    result = run_checked(command)
    if result.stderr:
        print(result.stderr.strip())


def load_obu_packets(ffprobe_path: str, obu_path: Path) -> list[dict]:
    command = [
        ffprobe_path,
        "-v",
        "error",
        "-show_packets",
        "-show_entries",
        "packet=pts_time,size,data",
        "-show_data",
        "-of",
        "json",
        str(obu_path),
    ]
    result = run_checked(command)
    payload = json.loads(result.stdout)
    packets = payload.get("packets", [])
    if not packets:
        raise RuntimeError("ffprobe returned no AV1 OBU packets.")
    return packets


def parse_hex_dump(hex_dump: str) -> bytes:
    if not hex_dump:
        return b""
    parts: list[str] = []
    for line in hex_dump.splitlines():
        line = line.strip()
        if not line or ":" not in line:
            continue
        _, remainder = line.split(":", 1)
        if "  " in remainder:
            remainder = remainder.split("  ", 1)[0]
        hex_chars = "".join(HEX_CHUNK_RE.findall(remainder))
        if hex_chars:
            parts.append(hex_chars)
    return bytes.fromhex("".join(parts))


def packet_to_payloads(packets: list[dict]) -> list[tuple[int, int, bytes]]:
    payloads: list[tuple[int, int, bytes]] = []
    for index, packet in enumerate(packets, start=1):
        packet_bytes = parse_hex_dump(packet.get("data", ""))
        declared_size = int(packet.get("size", 0))
        if declared_size != len(packet_bytes):
            raise RuntimeError(
                f"Packet {index} size mismatch: ffprobe declared {declared_size}, parsed {len(packet_bytes)}."
            )
        pts_time = float(packet.get("pts_time", "0") or 0.0)
        timestamp_ms = int(round(pts_time * 1000.0))
        payloads.append((index, timestamp_ms, packet_bytes))
    return payloads


def write_release_packet_file(
    payloads: list[tuple[int, int, bytes]],
    output_path: Path,
    max_fragment_payload: int,
    testing_id: int,
) -> dict:
    packet_count = 0
    total_payload_bytes = 0
    max_fragments_in_frame = 0

    with output_path.open("wb") as handle:
        for frame_id, timestamp_ms, frame_payload in payloads:
            total_payload_bytes += len(frame_payload)
            total_fragments = max(1, math.ceil(len(frame_payload) / max_fragment_payload))
            max_fragments_in_frame = max(max_fragments_in_frame, total_fragments)
            for fragment_id in range(total_fragments):
                start = fragment_id * max_fragment_payload
                end = min(start + max_fragment_payload, len(frame_payload))
                fragment = frame_payload[start:end]
                header = struct.pack(
                    RELEASE_PACKET_HEADER_FORMAT,
                    timestamp_ms,
                    frame_id,
                    0,
                    1,
                    fragment_id,
                    total_fragments,
                    len(fragment),
                    testing_id,
                )
                handle.write(header)
                handle.write(fragment)
                packet_count += 1

    return {
        "frame_count": len(payloads),
        "packet_count": packet_count,
        "payload_bytes": total_payload_bytes,
        "file_size": output_path.stat().st_size,
        "max_fragments_in_frame": max_fragments_in_frame,
    }


def validate_release_packet_file(
    release_packet_path: Path,
    expected_payloads: list[tuple[int, int, bytes]],
) -> None:
    frames: dict[int, dict] = {}
    with release_packet_path.open("rb") as handle:
        while True:
            header_bytes = handle.read(RELEASE_PACKET_HEADER_SIZE)
            if not header_bytes:
                break
            if len(header_bytes) != RELEASE_PACKET_HEADER_SIZE:
                raise RuntimeError("Release packet file ended with an incomplete header.")
            header = struct.unpack(RELEASE_PACKET_HEADER_FORMAT, header_bytes)
            (
                timestamp_ms,
                frame_id,
                split_id,
                total_splits,
                fragment_id,
                total_fragments,
                fragment_size,
                testing_id,
            ) = header
            if total_splits != 1 or split_id != 0:
                raise RuntimeError(f"Unexpected split layout for frame {frame_id}: splitId={split_id}, totalSplits={total_splits}.")
            if total_fragments <= 0:
                raise RuntimeError(f"Invalid totalFragments={total_fragments} for frame {frame_id}.")
            fragment = handle.read(fragment_size)
            if len(fragment) != fragment_size:
                raise RuntimeError(f"Release packet file ended mid-fragment for frame {frame_id}.")
            frame_entry = frames.setdefault(
                frame_id,
                {
                    "timestamp_ms": timestamp_ms,
                    "testing_id": testing_id,
                    "total_fragments": total_fragments,
                    "fragments": {},
                },
            )
            if frame_entry["timestamp_ms"] != timestamp_ms:
                raise RuntimeError(f"Inconsistent timestamp for frame {frame_id}.")
            if frame_entry["total_fragments"] != total_fragments:
                raise RuntimeError(f"Inconsistent fragment count for frame {frame_id}.")
            if fragment_id in frame_entry["fragments"]:
                raise RuntimeError(f"Duplicate fragment {fragment_id} in frame {frame_id}.")
            frame_entry["fragments"][fragment_id] = fragment

    if len(frames) != len(expected_payloads):
        raise RuntimeError(f"Frame count mismatch after validation: got {len(frames)}, expected {len(expected_payloads)}.")

    for frame_id, expected_timestamp_ms, expected_payload in expected_payloads:
        frame_entry = frames.get(frame_id)
        if frame_entry is None:
            raise RuntimeError(f"Missing frame {frame_id} in validated release packet file.")
        if frame_entry["timestamp_ms"] != expected_timestamp_ms:
            raise RuntimeError(
                f"Timestamp mismatch for frame {frame_id}: got {frame_entry['timestamp_ms']}, expected {expected_timestamp_ms}."
            )
        fragments = frame_entry["fragments"]
        if len(fragments) != frame_entry["total_fragments"]:
            raise RuntimeError(f"Frame {frame_id} is missing fragments after validation.")
        rebuilt = b"".join(fragments[index] for index in range(frame_entry["total_fragments"]))
        if rebuilt != expected_payload:
            raise RuntimeError(f"Payload mismatch after validation for frame {frame_id}.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Convert an AV1 video file into the Quest AV1 release-packet stream format used by this Unity project."
    )
    parser.add_argument("input", help="Input AV1 video file. Container formats such as WebM/Matroska are accepted.")
    parser.add_argument("output", help="Output binary file containing concatenated release packets.")
    parser.add_argument("--ffmpeg-path", help="Explicit ffmpeg executable path.")
    parser.add_argument("--ffprobe-path", help="Explicit ffprobe executable path.")
    parser.add_argument(
        "--max-fragment-payload",
        type=int,
        default=DEFAULT_MAX_FRAGMENT_PAYLOAD,
        help=f"Maximum bytes per fragment payload. Default: {DEFAULT_MAX_FRAGMENT_PAYLOAD}.",
    )
    parser.add_argument("--testing-id", type=int, default=0, help="Value written into the testingId header field.")
    parser.add_argument(
        "--keep-obu",
        action="store_true",
        help="Keep the intermediate low-overhead OBU file next to the output file.",
    )
    args = parser.parse_args()

    input_path = Path(args.input).resolve()
    output_path = Path(args.output).resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)

    if not input_path.exists():
        raise FileNotFoundError(f"Input file does not exist: {input_path}")
    if args.max_fragment_payload <= 0:
        raise ValueError("--max-fragment-payload must be positive.")

    ffmpeg_path = resolve_tool(args.ffmpeg_path, "FFMPEG_PATH", r"C:\Program Files\ffmpeg\bin\ffmpeg.exe", "ffmpeg")
    ffprobe_path = resolve_tool(args.ffprobe_path, "FFPROBE_PATH", r"C:\Program Files\ffmpeg\bin\ffprobe.exe", "ffprobe")

    keep_obu = args.keep_obu
    obu_path = output_path.with_suffix(".obu") if keep_obu else output_path.with_suffix(".obu.tmp")
    try:
        extract_obu_stream(ffmpeg_path, input_path, obu_path)
        packets = load_obu_packets(ffprobe_path, obu_path)
        payloads = packet_to_payloads(packets)
        stats = write_release_packet_file(payloads, output_path, args.max_fragment_payload, args.testing_id)
        validate_release_packet_file(output_path, payloads)
    finally:
        if not keep_obu and obu_path.exists():
            obu_path.unlink()

    summary = {"input": str(input_path), "output": str(output_path), **stats}
    if keep_obu:
        summary["obu"] = str(obu_path)
    print(json.dumps(summary, ensure_ascii=True, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as exc:
        if exc.stdout:
            print(exc.stdout, file=sys.stderr)
        if exc.stderr:
            print(exc.stderr, file=sys.stderr)
        raise
