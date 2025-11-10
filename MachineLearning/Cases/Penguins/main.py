
import seaborn as sns
data = sns.load_dataset("penguins").dropna()
data.head()
data.info()
data.describe()
data.isna().sum()

import pandas as pd
data = pd.get_dummies(data, drop_first=True)
data.to_csv("penguins_dataset.csv", index=False)



from sklearn.tree import DecisionTreeClassifier
model = DecisionTreeClassifier(max_depth=3, random_state=42)
#model.fit(X_train, y_train)


