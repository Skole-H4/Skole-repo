"""Exploratory data analysis helpers for tabular datasets."""
from __future__ import annotations

from typing import Iterable

import pandas as pd


def preview_dataframe(df: pd.DataFrame, *, rows: int = 5) -> pd.DataFrame:
    """Return the top rows of the dataframe for quick inspection."""
    return df.head(rows)


def dataframe_overview(df: pd.DataFrame) -> dict[str, object]:
    """Collect basic metadata metrics about the dataframe."""
    overview = {
        "shape": df.shape,
        "columns": list(df.columns),
        "dtypes": df.dtypes.astype(str).to_dict(),
    }
    overview["missing"] = df.isna().sum().to_dict()
    return overview


def describe_numeric(df: pd.DataFrame, *, percentiles: Iterable[float] | None = None) -> pd.DataFrame:
    """Return descriptive statistics for numeric columns."""
    return df.describe(percentiles=percentiles)
