from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import pandas as pd
import seaborn as sns
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (
	ConfusionMatrixDisplay,
	accuracy_score,
	classification_report,
	confusion_matrix,
)
from sklearn.model_selection import train_test_split
import numpy as np

TARGET_COLUMN = "big_tip"
RANDOM_STATE = 42
TEST_SIZE = 0.2


def load_dataset() -> pd.DataFrame:
	"""Load the tips dataset and create the engineered target."""

	raw = sns.load_dataset("tips")
	df = pd.get_dummies(raw, columns=["sex", "smoker", "day", "time"], drop_first=False)
	df[TARGET_COLUMN] = (df["tip"] > 3).astype(int)
	output_path = Path(__file__).with_name("tips_data.xlsx")
	#df.to_excel(output_path, index=False)
	return df


def split_features(df: pd.DataFrame) -> tuple[pd.DataFrame, pd.Series]:
	"""Split dataframe into feature matrix and target vector."""

	feature_columns = [col for col in df.columns if col not in {TARGET_COLUMN, "tip"}]
	X = df[feature_columns].copy()
	y = df[TARGET_COLUMN].copy()
	return X, y


def train_logistic_regression(
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	feature_names: list[str],
) -> dict[str, object]:
	"""Fit a logistic regression model and capture evaluation artefacts."""

	estimator = LogisticRegression(
		random_state=RANDOM_STATE,
		solver="liblinear",
		max_iter=1000,
	)
	estimator.fit(X_train, y_train)
	y_pred = estimator.predict(X_test)
	matrix = confusion_matrix(y_test, y_pred)
	accuracy = accuracy_score(y_test, y_pred)
	report_text = classification_report(y_test, y_pred)
	coeffs = estimator.coef_[0]

	return {
		"name": "Logistic Regression",
		"estimator": estimator,
		"matrix": matrix,
		"y_pred": y_pred,
		"y_test": y_test,
		"report": report_text,
		"accuracy": accuracy,
		"coefficients": pd.Series(
			coeffs,
			index=feature_names,
		).sort_values(key=np.abs, ascending=False),
	}


def display_results(result: dict[str, object]) -> None:
	"""Plot confusion matrix and feature impact for logistic regression."""

	fig, axes = plt.subplots(1, 2, figsize=(12, 5))

	ConfusionMatrixDisplay.from_predictions(
		result["y_test"],
		result["y_pred"],
		display_labels=["Small Tip", "Big Tip"],
		cmap="Blues",
		ax=axes[0],
	)
	axes[0].set_title("Logistic Regression Confusion Matrix")

	coeffs = result.get("coefficients")
	ax_coeff = axes[1]
	if isinstance(coeffs, pd.Series):
		coeffs.iloc[::-1].plot.barh(ax=ax_coeff, color="tab:orange")
		ax_coeff.set_title("Feature Impact (Logistic Coefficients)")
		ax_coeff.set_xlabel("Coefficient")
	else:
		ax_coeff.axis("off")

	fig.tight_layout()
	plt.show()


def main() -> None:
	df = load_dataset()
	X, y = split_features(df)

	X_train, X_test, y_train, y_test = train_test_split(
		X,
		y,
		test_size=TEST_SIZE,
		stratify=y,
		random_state=RANDOM_STATE,
	)

	result = train_logistic_regression(
		X_train,
		X_test,
		y_train,
		y_test,
		list(X_train.columns),
	)

	print(
		f"Logistic Regression accuracy: {result['accuracy']:.2%}",
		flush=True,
	)
	print(result["report"], flush=True)

	coeffs = result.get("coefficients")
	if isinstance(coeffs, pd.Series):
		print("Most impactful features for a big tip (logistic coefficients):")
		for feature, weight in coeffs.items():
			print(f"  {feature:<15} {weight:+.3f}")

	display_results(result)


if __name__ == "__main__":
	main()



