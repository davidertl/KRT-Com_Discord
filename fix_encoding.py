import sys, re

filepath = sys.argv[1]

with open(filepath, 'r', encoding='utf-8') as f:
    lines = f.readlines()

mojibake_pattern = re.compile(r'[\xc0-\xff][\x80-\xbf]')
fixed_count = 0
fixed_lines = []

for i, line in enumerate(lines):
    if mojibake_pattern.search(line):
        current = line
        layers_applied = 0
        for layer in range(30):
            try:
                decoded = current.encode('windows-1252').decode('utf-8')
                if decoded == current:
                    break
                current = decoded
                layers_applied += 1
            except (UnicodeEncodeError, UnicodeDecodeError):
                break
        if layers_applied > 0:
            fixed_lines.append(current)
            fixed_count += 1
            if fixed_count <= 15:
                clean = current.rstrip('\r\n')
                print(f"L{i+1} ({layers_applied}x): {clean[:120]}")
        else:
            fixed_lines.append(line)
    else:
        fixed_lines.append(line)

print(f"\nFixed {fixed_count} lines")

# Verify no mojibake remains
remaining = 0
for i, line in enumerate(fixed_lines):
    if re.search(r'Ã[\x80-\xbf]|ÃƒÆ|Â[^\s]', line):
        remaining += 1
        if remaining <= 3:
            print(f"STILL BAD L{i+1}: {line.rstrip()[:100]}")
if remaining:
    print(f"WARNING: {remaining} lines still have mojibake")
else:
    print("All mojibake resolved!")

if fixed_count > 0:
    with open(filepath, 'w', encoding='utf-8', newline='') as f:
        f.writelines(fixed_lines)
    print("File written successfully")