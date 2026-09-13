#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
实时屏幕H.264流分析工具
基于teacher_sim协议逆向，用于验证实时屏幕替换的可行性。

功能：
1. 发送feature 8触发学生端编码
2. 连接4806/TCP接收H.264流
3. 解析TKPC分片+HHRF记录
4. 统计I帧/P帧/SPS/PPS频率和大小
5. 保存原始H.264裸流
6. 尝试I帧替换验证（用ffmpeg预编码假帧替换IDR帧）

用法：
  python realtime_analyzer.py <学生端IP> [--channel 1] [--duration 30] [--replace]
"""

import socket
import struct
import time
import sys
import os
import subprocess
import threading
from collections import defaultdict

# ============ 协议常量（来自teacher_sim.py）============
SESSION_BASE_PORT = 5000
SESSION_PORT_STRIDE = 0x200  # 512
TCP_COMM_PORT = 4806

UMSP_VERSION = 0x10000
UMSP_SELECT_MAGIC = 0x4F434853  # SHCO
UMSP_DATA_MAGIC = 0x43504B54    # TKPC
DESK_CHANNEL = 10
MAX_FRAME_SIZE = 0x241800

MESS_MAGIC = 0x5353454D

# H.264 NAL单元类型
NAL_TYPE = {
    1: 'P帧(non-IDR)',
    5: 'I帧(IDR)',
    6: 'SEI',
    7: 'SPS',
    8: 'PPS',
}


def get_local_ip():
    """获取本机IP（连接外网时的出口IP）"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(('8.8.8.8', 80))
        return s.getsockname()[0]
    finally:
        s.close()


def get_session_port(channel):
    return SESSION_BASE_PORT + channel * SESSION_PORT_STRIDE


def build_mess_packet(sip, payload):
    """构造MESS包（单接收者）"""
    return (struct.pack('<III', MESS_MAGIC, 1, 1)
            + socket.inet_aton(sip)
            + payload)


def build_remote_view_payload(local_ip, sip, sport):
    """构造feature 8的617字节负载"""
    params = bytearray(604)
    struct.pack_into('<I', params, 0, 0)  # RemoteWithVoice
    params[4:8] = socket.inet_aton(local_ip)
    struct.pack_into('<H', params, 8, sport)   # 教师会话端点端口
    params[10:14] = socket.inet_aton(local_ip)
    struct.pack_into('<H', params, 14, sport)  # 对端端口
    struct.pack_into('<IIIIIIII', params, 36,
                     1,      # NetworkType
                     1,      # ShowMonitorControlMessage
                     20480,  # MaxSendSpeed
                     1,      # RepairMode
                     1440,   # MaxPacketSize
                     25,     # FrameLimit
                     75,     # CaptureQuality
                     1)      # TcpCommMode
    params[68:72] = socket.inet_aton(sip)
    struct.pack_into('<III', params, 72, 5, 12, 16)
    payload = struct.pack('<III', 617, 8, 0x80000000) + params + b'\x00'
    assert len(payload) == 617, f'feature 8长度错误: {len(payload)}'
    return payload


def build_remote_view_stop_payload():
    return struct.pack('<III', 13, 0, 0) + b'\x00'


def send_feature8(sip, channel, local_ip, enabled=True):
    """发送feature 8（启动/停止远程观看）"""
    sport = get_session_port(channel)
    payload = (build_remote_view_payload(local_ip, sip, sport) if enabled
               else build_remote_view_stop_payload())
    packet = build_mess_packet(sip, payload)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(('', 0))
    sock.sendto(packet, (sip, sport))
    sock.close()
    return sport


# ============ H.264 NAL分析 ============

def find_nal_units(data):
    """从H.264 Annex-B数据中提取NAL单元
    返回 [(nal_type, start_offset, length), ...]
    """
    nals = []
    i = 0
    n = len(data)
    while i < n - 4:
        # 查找起始码 00 00 00 01 或 00 00 01
        if data[i:i+4] == b'\x00\x00\x00\x01':
            start = i + 4
            nal_type = data[start] & 0x1F
            # 找下一个起始码
            j = start + 1
            while j < n - 3:
                if data[j:j+4] == b'\x00\x00\x00\x01' or data[j:j+3] == b'\x00\x00\x01':
                    break
                j += 1
            nals.append((nal_type, i, j - i))
            i = j
        elif data[i:i+3] == b'\x00\x00\x01':
            start = i + 3
            nal_type = data[start] & 0x1F
            j = start + 1
            while j < n - 3:
                if data[j:j+4] == b'\x00\x00\x00\x01' or data[j:j+3] == b'\x00\x00\x01':
                    break
                j += 1
            nals.append((nal_type, i, j - i))
            i = j
        else:
            i += 1
    return nals


def analyze_h264_payload(payload, frame_stats):
    """分析一帧H.264 payload，更新统计"""
    nals = find_nal_units(payload)
    has_idr = False
    has_sps = False
    has_pps = False
    nal_types = []
    for nal_type, offset, length in nals:
        name = NAL_TYPE.get(nal_type, f'未知({nal_type})')
        nal_types.append(f'{name}({length}B)')
        frame_stats['nal_count'][nal_type] += 1
        frame_stats['nal_bytes'][nal_type] += length
        if nal_type == 5:
            has_idr = True
        elif nal_type == 7:
            has_sps = True
        elif nal_type == 8:
            has_pps = True
    return has_idr, has_sps, has_pps, nal_types


# ============ TKPC分片重组 ============

class FrameReassembler:
    """HHRF/HJRF帧分片重组器"""
    def __init__(self):
        self.fragments = {}

    def handle_fragment(self, data):
        """处理一个TKPC分片，返回完整帧或None"""
        if len(data) < 12:
            return None
        frame_seq, offset, total = struct.unpack_from('<III', data)
        fragment = data[12:]
        if not 1 <= total <= MAX_FRAME_SIZE:
            return None
        if offset > total or len(fragment) > total - offset:
            return None
        state = self.fragments.get(frame_seq)
        if state is None or state['total'] != total:
            state = {'total': total, 'data': bytearray(total),
                     'seen': bytearray(total), 'received': 0}
            self.fragments[frame_seq] = state
            if len(self.fragments) > 8:
                oldest = next(iter(self.fragments))
                if oldest != frame_seq:
                    self.fragments.pop(oldest, None)
        end = offset + len(fragment)
        new_bytes = len(fragment) - sum(state['seen'][offset:end])
        state['data'][offset:end] = fragment
        state['seen'][offset:end] = b'\x01' * len(fragment)
        state['received'] += new_bytes
        if state['received'] == total:
            record = bytes(state['data'])
            self.fragments.pop(frame_seq, None)
            return frame_seq, record
        return None


def parse_record(record):
    """解析64字节记录头，返回 (magic, encoded_rect, visible_rect, frame_type, payload)"""
    if len(record) < 64:
        return None
    magic, declared_size, timestamp = struct.unpack_from('<III', record)
    encoded_rect = struct.unpack_from('<iiii', record, 12)
    visible_rect = struct.unpack_from('<iiii', record, 28)
    frame_type = struct.unpack_from('<I', record, 44)[0]
    payload = record[64:]
    return magic, encoded_rect, visible_rect, frame_type, payload


# ============ 主流程 ============

def analyze_stream(sip, channel=1, duration=30, do_replace=False, ffmpeg_path=None):
    """连接学生端接收并分析H.264流"""
    local_ip = get_local_ip()
    print(f'[信息] 本机IP: {local_ip}')
    print(f'[信息] 学生端: {sip}:{TCP_COMM_PORT} (频道={channel})')
    print(f'[信息] 分析时长: {duration}秒')
    if do_replace:
        print(f'[信息] I帧替换验证: 开启')

    # 1. 发送feature 8
    sport = send_feature8(sip, channel, local_ip, enabled=True)
    print(f'[信息] 已发送feature 8 -> {sip}:{sport}')
    time.sleep(0.5)

    # 2. 连接4806
    print(f'[信息] 正在连接 {sip}:{TCP_COMM_PORT} ...')
    conn = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    conn.settimeout(5)
    try:
        conn.connect((sip, TCP_COMM_PORT))
    except OSError as e:
        print(f'[错误] 连接失败: {e}')
        send_feature8(sip, channel, local_ip, enabled=False)
        return
    # SHCO握手
    hello = struct.pack('<IIIII', UMSP_VERSION, UMSP_SELECT_MAGIC, 8, 1, DESK_CHANNEL)
    conn.sendall(hello)
    conn.settimeout(2.0)
    print(f'[信息] 已连接，SHCO channel={DESK_CHANNEL}')

    # 统计
    frame_stats = {
        'total_frames': 0,
        'h264_frames': 0,
        'jpeg_frames': 0,
        'idr_frames': 0,
        'nal_count': defaultdict(int),
        'nal_bytes': defaultdict(int),
        'first_idr_time': None,
        'last_idr_time': None,
        'idr_intervals': [],
        'encoded_size': None,
        'visible_size': None,
        'frame_sizes': [],
    }

    # 保存原始流
    output_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'realtime_analysis')
    os.makedirs(output_dir, exist_ok=True)
    raw_h264_path = os.path.join(output_dir, f'raw_{sip.replace(".","_")}.h264')
    raw_file = open(raw_h264_path, 'wb')

    # I帧替换
    fake_idr_data = None
    replaced_h264_path = None
    replaced_file = None
    if do_replace:
        replaced_h264_path = os.path.join(output_dir, f'replaced_{sip.replace(".","_")}.h264')
        replaced_file = open(replaced_h264_path, 'wb')
        fake_idr_data = generate_fake_idr(ffmpeg_path, output_dir)
        if fake_idr_data:
            print(f'[信息] 假I帧已生成: {len(fake_idr_data)}字节')
        else:
            print('[警告] 假I帧生成失败，将只统计不替换')

    reassembler = FrameReassembler()
    start_time = time.time()
    buffer = b''

    print(f'\n{"="*60}')
    print(f'{"帧号":>4} {"类型":>6} {"编码尺寸":>12} {"可见尺寸":>12} {"payload":>8} {"NAL单元"}')
    print(f'{"="*60}')

    try:
        while time.time() - start_time < duration:
            try:
                data = conn.recv(65536)
            except socket.timeout:
                continue
            if not data:
                print('[信息] 连接已关闭')
                break
            buffer += data

            # 解析TKPC包 (UMSP外层)
            # 结构: [version(4)][magic(4)][payload_len(4)][channel(4)][分片数据...]
            while len(buffer) >= 12:
                version, magic, payload_len = struct.unpack_from('<III', buffer)
                if payload_len > MAX_FRAME_SIZE + 0x10000:
                    # 非法长度，跳过1字节重新同步
                    buffer = buffer[1:]
                    continue
                packet_len = 12 + payload_len
                if len(buffer) < packet_len:
                    break  # 数据不完整
                payload = bytes(buffer[12:packet_len])
                buffer = buffer[packet_len:]

                if magic != UMSP_DATA_MAGIC:
                    continue
                if len(payload) < 4:
                    continue
                channel = struct.unpack_from('<I', payload)[0]
                if channel != DESK_CHANNEL:
                    continue

                # 桌面分片数据: payload[4:]
                fragment_data = payload[4:]

                # 重组分片
                result = reassembler.handle_fragment(fragment_data)
                if result is None:
                    continue
                frame_seq, record = result

                # 解析记录
                parsed = parse_record(record)
                if parsed is None:
                    continue
                magic, encoded_rect, visible_rect, frame_type, payload = parsed

                frame_stats['total_frames'] += 1
                enc_w = abs(encoded_rect[2] - encoded_rect[0])
                enc_h = abs(encoded_rect[3] - encoded_rect[1])
                vis_w = abs(visible_rect[2] - visible_rect[0])
                vis_h = abs(visible_rect[3] - visible_rect[1])
                frame_stats['encoded_size'] = (enc_w, enc_h)
                frame_stats['visible_size'] = (vis_w, vis_h)
                frame_stats['frame_sizes'].append(len(payload))

                if magic == 0x46524848:  # HHRF = H.264
                    frame_stats['h264_frames'] += 1
                    has_idr, has_sps, has_pps, nal_types = analyze_h264_payload(payload, frame_stats)

                    if has_idr:
                        frame_stats['idr_frames'] += 1
                        now = time.time()
                        if frame_stats['first_idr_time'] is None:
                            frame_stats['first_idr_time'] = now
                        if frame_stats['last_idr_time'] is not None:
                            interval = now - frame_stats['last_idr_time']
                            frame_stats['idr_intervals'].append(interval)
                        frame_stats['last_idr_time'] = now

                    # 保存原始流
                    raw_file.write(payload)

                    # I帧替换
                    if do_replace and fake_idr_data and replaced_file:
                        if has_idr:
                            # 替换：保留SPS/PPS，替换IDR和后续P帧
                            # 简化：直接用假I帧替换整帧payload
                            replaced_file.write(fake_idr_data)
                        else:
                            replaced_file.write(payload)

                    if frame_stats['total_frames'] <= 20 or frame_stats['total_frames'] % 50 == 0:
                        nal_str = ', '.join(nal_types[:4])
                        if len(nal_types) > 4:
                            nal_str += f'...(共{len(nal_types)}个)'
                        idr_mark = ' [IDR]' if has_idr else ''
                        print(f'{frame_stats["total_frames"]:>4} {"H264":>6} '
                              f'{enc_w}x{enc_h:<6} {vis_w}x{vis_h:<6} '
                              f'{len(payload):>8} {nal_str}{idr_mark}')

                elif magic == 0x46524A48:  # HJRF = JPEG
                    frame_stats['jpeg_frames'] += 1
                    if frame_stats['total_frames'] <= 20:
                        print(f'{frame_stats["total_frames"]:>4} {"JPEG":>6} '
                              f'{enc_w}x{enc_h:<6} {vis_w}x{vis_h:<6} '
                              f'{len(payload):>8}')
                else:
                    if frame_stats['total_frames'] <= 5:
                        print(f'{frame_stats["total_frames"]:>4} {str(magic):>10} '
                              f'{enc_w}x{enc_h:<6} {vis_w}x{vis_h:<6} '
                              f'{len(payload):>8}')

    except KeyboardInterrupt:
        print('\n[信息] 用户中断')
    finally:
        raw_file.close()
        if replaced_file:
            replaced_file.close()
        conn.close()
        send_feature8(sip, channel, local_ip, enabled=False)
        print('[信息] 已发送停止feature 8')

    # 输出统计
    elapsed = time.time() - start_time
    print(f'\n{"="*60}')
    print(f'分析统计 (时长: {elapsed:.1f}秒)')
    print(f'{"="*60}')
    print(f'总帧数: {frame_stats["total_frames"]}')
    print(f'H.264帧: {frame_stats["h264_frames"]} ({frame_stats["h264_frames"]/max(elapsed,0.1):.1f} fps)')
    print(f'JPEG帧: {frame_stats["jpeg_frames"]}')
    print(f'编码尺寸: {frame_stats["encoded_size"]}')
    print(f'可见尺寸: {frame_stats["visible_size"]}')
    if frame_stats['frame_sizes']:
        avg_size = sum(frame_stats['frame_sizes']) / len(frame_stats['frame_sizes'])
        print(f'平均帧大小: {avg_size:.0f}字节')
        print(f'码率估算: {avg_size * frame_stats["h264_frames"] / max(elapsed,0.1) * 8 / 1000:.0f} kbps')

    print(f'\nI帧(IDR)统计:')
    print(f'  IDR帧数: {frame_stats["idr_frames"]}')
    if frame_stats['idr_intervals']:
        avg_interval = sum(frame_stats['idr_intervals']) / len(frame_stats['idr_intervals'])
        print(f'  平均I帧间隔: {avg_interval:.2f}秒 (约每{avg_interval * 25:.0f}帧一个I帧)')
        print(f'  I帧间隔范围: {min(frame_stats["idr_intervals"]):.2f} ~ {max(frame_stats["idr_intervals"]):.2f}秒')
    elif frame_stats['idr_frames'] == 1:
        print(f'  仅在连接开始时检测到1个I帧，{elapsed:.1f}秒内未出现第二个I帧')
        print(f'  说明：极域编码器可能采用"仅首帧I帧+后续全P帧"策略，或I帧间隔 > {elapsed:.0f}秒')
    else:
        print(f'  未检测到I帧（可能时长太短或编码参数不同）')

    print(f'\nNAL单元统计:')
    for nal_type in sorted(frame_stats['nal_count'].keys()):
        name = NAL_TYPE.get(nal_type, f'未知({nal_type})')
        count = frame_stats['nal_count'][nal_type]
        total_bytes = frame_stats['nal_bytes'][nal_type]
        print(f'  {name}: {count}个, 共{total_bytes}字节')

    print(f'\n输出文件:')
    print(f'  原始H.264流: {raw_h264_path}')
    if replaced_h264_path and os.path.exists(replaced_h264_path):
        print(f'  替换后H.264流: {replaced_h264_path}')
        print(f'  验证命令: ffplay {replaced_h264_path}')

    # 可行性结论
    print(f'\n{"="*60}')
    print(f'实时屏幕替换可行性评估')
    print(f'{"="*60}')
    if frame_stats['idr_frames'] >= 2:
        avg_interval = sum(frame_stats['idr_intervals']) / len(frame_stats['idr_intervals'])
        print(f'✅ 检测到多个I帧，平均间隔{avg_interval:.2f}秒')
        print(f'   网络层I帧替换可行：每{avg_interval:.1f}秒可替换一次画面')
        print(f'   限制：P帧期间画面保持运动，替换后到下一个I帧前会有画面撕裂')
    elif frame_stats['idr_frames'] == 1:
        print(f'⚠️  仅首帧为I帧，{elapsed:.0f}秒内无第二个I帧')
        print(f'   极域编码器策略：首帧IDR + 后续全P帧（无周期性I帧）')
        print(f'   网络层仅替换I帧 = 只能替换首帧，P帧会恢复原始画面')
        print(f'   结论：纯网络层I帧替换不可行，需替换每帧或触发强制I帧')
        print(f'   替代方向：学生端编码函数Hook（替换编码前的原始帧）')
    else:
        print(f'⚠️  未检测到I帧，需要更长时间分析或检查编码参数')
    print(f'   H.264 Annex-B格式确认，U/V色度交换需在编码前处理')


def generate_fake_idr(ffmpeg_path, output_dir):
    """用ffmpeg生成一张静态图片的H.264 IDR帧"""
    if not ffmpeg_path:
        # 尝试同目录
        ffmpeg_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ffmpeg.exe')
    if not os.path.exists(ffmpeg_path):
        print('[警告] 未找到ffmpeg.exe，无法生成假I帧')
        return None

    # 生成一张纯色图片（红色）作为假屏幕
    fake_img_path = os.path.join(output_dir, 'fake_screen.png')
    fake_h264_path = os.path.join(output_dir, 'fake_idr.h264')

    # 用ffmpeg生成1帧1152x768的红色图片并编码为H.264
    # 注意：极域使用U/V交换，这里生成标准H.264，替换后颜色会偏色
    # 实际替换时需要先交换U/V再编码
    cmd = [
        ffmpeg_path,
        '-y',
        '-f', 'lavfi',
        '-i', f'color=c=red:s=1152x768:d=1:r=1',
        '-c:v', 'libx264',
        '-profile:v', 'baseline',
        '-level', '3.1',
        '-x264-params', 'keyint=1:min-keyint=1',
        '-pix_fmt', 'yuv420p',
        '-an',
        '-f', 'h264',
        fake_h264_path
    ]
    try:
        result = subprocess.run(cmd, capture_output=True, timeout=30)
        if os.path.exists(fake_h264_path):
            with open(fake_h264_path, 'rb') as f:
                data = f.read()
            # 只保留第一个IDR帧（包含SPS/PPS/IDR）
            nals = find_nal_units(data)
            idr_end = 0
            for nal_type, offset, length in nals:
                if nal_type == 5:
                    idr_end = offset + length
                    break
            if idr_end > 0:
                return data[:idr_end]
            return data
    except Exception as e:
        print(f'[警告] ffmpeg生成假I帧失败: {e}')
    return None


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    sip = sys.argv[1]
    channel = 1
    duration = 30
    do_replace = False
    ffmpeg_path = None

    i = 2
    while i < len(sys.argv):
        if sys.argv[i] == '--channel' and i + 1 < len(sys.argv):
            channel = int(sys.argv[i + 1])
            i += 2
        elif sys.argv[i] == '--duration' and i + 1 < len(sys.argv):
            duration = int(sys.argv[i + 1])
            i += 2
        elif sys.argv[i] == '--replace':
            do_replace = True
            i += 1
        elif sys.argv[i] == '--ffmpeg' and i + 1 < len(sys.argv):
            ffmpeg_path = sys.argv[i + 1]
            i += 2
        else:
            print(f'未知参数: {sys.argv[i]}')
            sys.exit(1)

    analyze_stream(sip, channel, duration, do_replace, ffmpeg_path)


if __name__ == '__main__':
    main()
