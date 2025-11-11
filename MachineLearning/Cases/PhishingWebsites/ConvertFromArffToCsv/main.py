import pandas as pd
import arff as liac_arff
from pathlib import Path

# Resolve paths relative to this script
root = Path(__file__).resolve().parents[1]  # points to PhishingWebsites
input_arff = root / 'Data' / 'Original' / 'phishing_websites.arff'
output_csv = root / 'Data' / 'Converted' / 'phishing_websites.csv'

if not input_arff.exists():
	raise FileNotFoundError(f"ARFF file not found: {input_arff}")

output_csv.parent.mkdir(parents=True, exist_ok=True)

# liac-arff returns a dict-like structure with 'attributes' and 'data'
with input_arff.open('r', encoding='utf-8', errors='replace') as f:
	dataset = liac_arff.load(f)

columns = [attr[0] for attr in dataset['attributes']]
df = pd.DataFrame(dataset['data'], columns=columns)
df.to_csv(output_csv, index=False)
print(f"Saved CSV: {output_csv}")