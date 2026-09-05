import os
import shutil

data_root = os.environ.get("DATA_ROOT", "./data")
out_root = os.environ.get("OUT_ROOT", "./out")

os.makedirs(out_root + r'\frames', exist_ok=True)
os.makedirs(out_root + r'\audio', exist_ok=True)
os.makedirs(out_root + r'\Annotations', exist_ok=True)

for folder in os.listdir(data_root + r'\Data'):
    folder_path = os.path.join(data_root, 'Data', folder)
    if not os.path.isdir(folder_path):
        continue
    for fn in os.listdir(folder_path):
        src = os.path.join(folder_path, fn)
        if fn.endswith('.jpg'):
            shutil.copy(src, os.path.join(out_root, 'frames', fn))
        elif fn.endswith('.wav'):
            shutil.copy(src, os.path.join(out_root, 'audio', fn))

for fn in os.listdir(data_root + r'\Annotations'):
    src = os.path.join(data_root, 'Annotations', fn)
    shutil.copy(src, os.path.join(out_root, 'Annotations', fn + '.xml'))

print('Done!')