# Penguins Exploration Scaffold

This scaffold provides a small CLI for loading the seaborn sample datasets (default: penguins), performing a quick dataframe inspection, and generating exploratory plots.

## Getting started

1. Create a virtual environment (PowerShell example):
   ```pwsh
   python -m venv .venv
   .venv\Scripts\Activate.ps1
   ```
2. Install dependencies:
   ```pwsh
   pip install -r requirements.txt
   ```
3. Run the exploration script:
   ```pwsh
   python main.py --dataset penguins --preview-rows 10 --describe --plot bill_length_mm bill_depth_mm species
   ```

## Features

- Fetch any seaborn dataset and optionally cache a CSV snapshot under a chosen directory.
- Display dataframe shape, dtypes, and missing value counts.
- Print descriptive statistics for numeric columns (`--describe`).
- Generate scatter plots with an optional hue grouping (`--plot X Y [HUE]`).
