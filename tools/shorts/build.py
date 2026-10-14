# python3 build.py <plan.json> — монтаж ролика и обложки по плану. Запускать из рабочей папки языка (см. make.sh):
# там лежат vo/ (озвучка), caps/ (подписи), а снятые клипы — в ../clips, музыка — ../music.wav.
import json, subprocess, os, sys

def dur(p): return float(subprocess.check_output(['ffprobe','-v','error','-show_entries','format=duration','-of','csv=p=0',p]))
def frames_of(clip): return json.load(open(f'../clips/{clip}/frames.json'))
def abs_frame(clip, f): return os.path.abspath(os.path.join('..', f['file']))

cfg = json.load(open(sys.argv[1]))
# Каждый план: клип, начало в клипе (с), ускорение, реплика, подпись. Длина плана = реплика + 0,45 с (финал + 0,8 с).
PLAN = cfg['plan']
out = cfg['out']
os.makedirs('seg', exist_ok=True)
t = 0; vo_inputs = []; parts = []
for i,(clip,start,speed,vo,cap) in enumerate(PLAN):
    d = round(dur(f'vo/{vo}.aiff') + 0.45, 3)
    if cap == 'end': d += 0.8
    frames = frames_of(clip)
    t0 = frames[0]['t']
    with open(f'seg/{i}.txt','w') as f:
        for a,b in zip(frames, frames[1:]):
            f.write(f"file '{abs_frame(clip, a)}'\nduration {b['t']-a['t']:.4f}\n")
        f.write(f"file '{abs_frame(clip, frames[-1])}'\n")
    span = d*speed
    total = frames[-1]['t'] - t0
    # Кусок длиннее записи: сдвинуть начало, поднять ускорение или переснять.
    if start + span > total: print('short clip', clip, round(start+span, 2), round(total, 2))
    vf = f"fps=60,trim=start={start}:duration={span},setpts=(PTS-STARTPTS)/{speed},fps=60"
    if cap == 'end':
        vf += ",boxblur=12:2,eq=brightness=-0.15"
    subprocess.run(['ffmpeg','-v','error','-y','-f','concat','-safe','0','-i',f'seg/{i}.txt','-i',f'caps/{cap}.png',
        '-filter_complex',f"[0]{vf}[v];[v][1]overlay=enable='gte(t,0.15)',format=yuv420p",'-t',str(d),'-r','60','-c:v','libx264','-crf','17','-preset','medium',f'seg/{i}.mp4'],check=True)
    parts.append(f'seg/{i}.mp4'); vo_inputs.append((f'vo/{vo}.aiff', t+0.15)); t += d
open('seg/list.txt','w').write(''.join(f"file '{os.path.abspath(p)}'\n" for p in parts))
subprocess.run(['ffmpeg','-v','error','-y','-f','concat','-safe','0','-i','seg/list.txt','-c','copy','seg/video.mp4'],check=True)
args=['ffmpeg','-v','error','-y','-i','seg/video.mp4','-i','../music.wav']
fl=[]
for k,(p,at) in enumerate(vo_inputs):
    args += ['-i',p]; fl.append(f"[{k+2}]adelay={int(at*1000)}|{int(at*1000)},volume=1.6[v{k}]")
mix=''.join(f'[v{k}]' for k in range(len(vo_inputs)))
fl.append(f"{mix}amix=inputs={len(vo_inputs)}:normalize=0[vo]")
fl.append(f"[1]atrim=0:{t},volume=0.55,afade=t=in:d=0.5,afade=t=out:st={t-1.5}:d=1.5[m]")
fl.append("[vo][m]amix=inputs=2:normalize=0,loudnorm=I=-14:TP=-1.5[a]")
args += ['-filter_complex',';'.join(fl),'-map','0:v','-map','[a]','-c:v','copy','-c:a','aac','-b:a','192k','-shortest',f'{out}.mp4']
subprocess.run(args,check=True)

# Обложка 1080×1920: кадр клипа и подпись «cover» поверх.
cover = cfg.get('cover')
if cover:
    frame = abs_frame(cover['clip'], frames_of(cover['clip'])[cover['frame']])
    sat = cover.get('saturation', 1)
    subprocess.run(['ffmpeg','-v','error','-y','-i',frame,'-i','caps/cover.png','-filter_complex',
        f'[0]scale=1080:1920,eq=saturation={sat}[b];[b][1]overlay','-q:v','2',f'{out}-cover.jpg'],check=True)
print('total', round(t,2), '→', f'{out}.mp4')
