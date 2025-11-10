import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
from sklearn.linear_model import LinearRegression
import joblib

CSV_PATH = "Salary_dataset.csv"  # forventer kolonnerne: YearsExperience, Salary

def main():
    # Indlæs og kig på data
    df = pd.read_csv(CSV_PATH)
    print(df.head())

    # Scatter-plot over YearsExperience vs. Salary
    plt.figure()
    plt.scatter(df["YearsExperience"], df["Salary"], alpha=0.85)
    plt.title("Years of Experience vs. Salary")
    plt.xlabel("Years of Experience")
    plt.ylabel("Salary")
    plt.tight_layout()
    # plt.show()

    # Forbered X (2D) og y (1D)
    # sklearn forventer X som 2D (n_samples, n_features)
    X = df[["YearsExperience"]]      # 2D (n,1)
    y = df["Salary"]                 # 1D (n,)

    # Træn lineær regression (mindste kvadraters metode)
    model = LinearRegression().fit(X, y)

    intercept = float(model.intercept_)  # estimeret løn ved 0 års erfaring (b0)
    slope = float(model.coef_[0])        # lønstigning pr. ekstra års erfaring (b1)
    print(f"Model: ŷ = {slope:.2f} * YearsExperience + {intercept:.2f}")
    print(f"Hældning: +{slope:.2f} lønenheder pr. ekstra år erfaring")
    print(f"Intercept (0 år): {intercept:.2f} (ekstrapolation hvis få data nær 0)")

    # Ekstra forklaring
    n = len(df)
    x_min_data, x_max_data = df["YearsExperience"].min(), df["YearsExperience"].max()
    print(f"Antal observationer: {n}. Spænd i erfaring: {x_min_data:.1f}–{x_max_data:.1f} år")

    # R^2 score (forklaringsgrad)
    r2 = model.score(X, y)
    print(f"Forklaringsgrad (R^2): {r2:.3f}")

    # Eksempel-forudsigelser ved repræsentative punkter (start, midte, slut af data)
    example_years = [round(x_min_data, 1), round((x_min_data + x_max_data)/2, 1), round(x_max_data, 1)]
    print("Eksempel-forudsigelser:")
    for yrs in example_years:
        # Brug DataFrame for at bevare kolonnenavnet og undgå sklearn advarsel om feature names
        pred_salary = float(model.predict(pd.DataFrame({'YearsExperience':[yrs]}))[0])
        label = "(ekstrapolation)" if yrs < x_min_data or yrs > x_max_data else ""
        print(f"  {yrs:.1f} år erfaring → forventet løn: {pred_salary:,.2f} {label}")

    # Eksempel: enkel forudsigelse (demonstration)
    years_demo = 5.0
    if x_min_data <= years_demo <= x_max_data:
        demo_pred = float(model.predict(pd.DataFrame({'YearsExperience':[years_demo]}))[0])
        print(f"Demo: {years_demo:.1f} år erfaring → {demo_pred:,.2f} i forventet løn")
    else:
        print(f"Demo-år {years_demo} ligger uden for datasættets spænd; springer over.")

    # Tegn regressionslinjen glat hen over dataområdet
    x_min, x_max = df["YearsExperience"].min(), df["YearsExperience"].max()
    X_line = pd.DataFrame({"YearsExperience": np.linspace(x_min, x_max, 200)})  # 200 jævnt fordelte punkter
    y_line = model.predict(X_line)

    plt.figure()
    plt.scatter(df["YearsExperience"], df["Salary"], alpha=0.85, label="Data")
    plt.plot(X_line["YearsExperience"], y_line, label="Linear Regression")
    plt.title("Linear Regression Fit")
    plt.xlabel("YearsExperience")
    plt.ylabel("Salary")
    plt.legend()
    plt.tight_layout()
    plt.show()

    joblib.dump(model, 'salary_model.joblib')
    print("Model gemt som salary_model.joblib")

if __name__ == "__main__":
    main()
