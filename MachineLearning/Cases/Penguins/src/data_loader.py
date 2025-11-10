"""Utilities for fetching sample datasets from seaborn."""
from __future__ import annotations

from pathlib import Path
from typing import Optional

import pandas as pd
import seaborn as sns


DEFAULT_DATASET = "penguins"


def load_seaborn_dataset(name: str = DEFAULT_DATASET, *, cache_dir: Optional[Path] = None) -> pd.DataFrame:
    """Load a dataset from seaborn and optionally persist a cached copy.

    Parameters
    ----------
    name:
        Name of the seaborn dataset to load.
    cache_dir:
        Optional directory where a CSV snapshot of the dataset will be written.

    Returns
    -------
    pandas.DataFrame
        The loaded dataset.
    """

    df = sns.load_dataset(name).copy()

    if cache_dir:
        cache_dir.mkdir(parents=True, exist_ok=True)
        output_path = cache_dir / f"{name}.csv"
        df.to_csv(output_path, index=False)

    return df
