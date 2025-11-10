"""Simple plotting utilities for the penguins dataset."""
from __future__ import annotations

from pathlib import Path
from typing import Optional

import matplotlib.pyplot as plt
import pandas as pd
import seaborn as sns


def scatter_pair(
    df: pd.DataFrame,
    x: str,
    y: str,
    *,
    hue: Optional[str] = None,
    save_dir: Optional[Path] = None,
) -> Path:
    """Create a scatterplot and save it to disk."""
    sns.set_theme(style="whitegrid")
    fig, ax = plt.subplots(figsize=(8, 6))
    sns.scatterplot(data=df, x=x, y=y, hue=hue, ax=ax)
    ax.set_title(f"{y} vs {x}")

    save_dir = save_dir or Path("outputs") / "figures"
    save_dir.mkdir(parents=True, exist_ok=True)
    output_path = save_dir / f"scatter_{x}_vs_{y}.png"
    fig.tight_layout()
    fig.savefig(output_path)
    plt.close(fig)
    return output_path
