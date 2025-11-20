import pandas as pd
from pathlib import Path
import arff as liac_arff

root = Path(__file__).resolve().parents[1]
csv_path = root / 'Data' / 'Converted' / 'phishing_websites.csv'
arff_path = root / 'Data' / 'Original' / 'phishing_websites.arff'

def load_df():
    if csv_path.exists():
        df = pd.read_csv(csv_path)
        return df
    if arff_path.exists():
        with arff_path.open('r', encoding='utf-8', errors='replace') as f:
            dataset = liac_arff.load(f)
        cols = [a[0] for a in dataset['attributes']]
        df = pd.DataFrame(dataset['data'], columns=cols)
        return df
    raise FileNotFoundError('No CSV or ARFF found; please run the converter first')

def summarize(df):
    # Ensure Result exists
    if 'Result' not in df.columns:
        print('No Result column found; cannot interpret labels')
        return

    print(f"Dataset rows: {len(df)}\n")

    labels = df['Result'].unique()
    print(f"Label values (Result): {labels}\n")

    # For each column, show unique values and counts per Result
    for col in df.columns:
        if col == 'Result':
            continue
        vals = sorted(df[col].dropna().unique())
        print(f"{col}: values={vals}")
        counts = df.groupby(['Result', col]).size().unstack(fill_value=0)
        print(counts)

        # Find which value is most common when Result==1 (phishing) and Result==-1 (legit)
        if 1 in df['Result'].values:
            row = counts.loc[1]
            most_phish = row.idxmax()
        else:
            most_phish = None
        if -1 in df['Result'].values:
            row2 = counts.loc[-1]
            most_legit = row2.idxmax()
        else:
            most_legit = None

        print(f"  -> most associated with Result=1: {most_phish}; with Result=-1: {most_legit}\n")

if __name__ == '__main__':
    df = load_df()
    summarize(df)
