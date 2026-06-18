# GitHub Initialization Instructions

Follow these steps to initialize your new GitHub repository and push the project.

### 1. Initialize Git
Open a terminal in the project root (`c:\Users\spuri\source\repos\PolarsPlus\Glacier.SpanCoder`) and run:
```powershell
git init
```

### 2. Add Files
Add all files (the `.gitignore` will ensure build artifacts and the local `nuget-local` package cache are not included):
```powershell
git add .
```

### 3. Commit
Create your first commit:
```powershell
git commit -m "Initial Commit: SpanCoder IDE Core v1.0.0.0"
```

### 4. Create Repository on GitHub
1. Go to [GitHub](https://github.com/new).
2. Create a new repository named `Glacier.SpanCoder` (or your preferred name).
3. Do **not** initialize with a README, .gitignore, or license (we already have them).

### 5. Link and Push
```powershell
git remote add origin https://github.com/ian-cowley/Glacier.SpanCoder.git
git branch -M main
git push -u origin main
```

### 6. Create Tag & Release
Tag this initial build for the release:
```powershell
git tag -a v1.0.0.0 -m "Initial Release v1.0.0.0"
git push origin v1.0.0.0
```
