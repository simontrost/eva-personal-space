# Working in your own EVA repository

The shared source repository is:

<https://www.socialvrlab.com/gitea/oxidiert/EVA>

You may clone and pull this repository and make local commits. Publish your
changes to a repository in your own account. Cloning does not grant permission
to push to `oxidiert/EVA`; Gitea enforces access on the server.

## Recommended: fork first

1. Sign in to SocialVRLab Gitea with your own account.
2. Open the shared repository, click **Fork**, and choose your own account as
   the owner. Copy the HTTPS clone URL shown on your fork.
3. Install Git and Git LFS, then clone your fork. Replace `YOUR_USERNAME` below
   with your SocialVRLab username:

```powershell
git lfs install
git clone https://www.socialvrlab.com/gitea/YOUR_USERNAME/EVA.git
cd EVA
git lfs pull
git remote add upstream https://www.socialvrlab.com/gitea/oxidiert/EVA.git
git remote set-url --push upstream DISABLED
```

`origin` is your own repository. `upstream` fetches updates from the shared
source. The disabled push URL helps prevent mistakes in this checkout; the
server permissions provide the actual protection.

After making changes, review and commit the files you intend to publish:

```powershell
git status
git add <files-you-changed>
git commit -m "Describe your changes"
git push -u origin main
```

Use your current branch name instead of `main` when working on another branch.
For updates from the shared source, commit or stash your work first, then run:

```powershell
git fetch upstream
git merge upstream/main
```

You can offer changes through a pull request from your fork. The repository
owner decides whether to merge it.

## Alternative: use an independent repository

Create an empty repository in your own account on a Git host that supports
Git LFS. Do not initialize it with a README, license or .gitignore. In a clone
of the shared source, replace the example URL with your new repository's URL:

```powershell
git remote rename origin upstream
git remote set-url --push upstream DISABLED
git remote add origin https://YOUR_GIT_HOST/YOUR_USERNAME/EVA.git
git lfs push --all origin
git push -u origin main
```

The explicit LFS upload transfers the avatar models and textures to the new
host. Run this alternative only in your own clone, rather than in the owner's
working project.

## Access policy for the owner

- Keep `oxidiert` as the repository owner.
- This public repository can be cloned without adding collaborators.
- Give other people **Read** access if explicit collaborator access is needed.
  Do not grant **Write** or **Admin** access to people who should work in forks.
- Keep deployment keys read-only. Do not share the owner's password, tokens
  or SSH keys.
- A protected `main` branch can additionally limit pushes and merges to
  `oxidiert`, with force pushes disabled. Read-only repository access applies
  to every branch, including new branches and tags.

Git permissions do not prevent local commits or control what someone publishes
in their own repository. Project and third-party license terms still apply.

Gitea's permission model:
<https://docs.gitea.com/1.27/usage/permissions/>.
