package ops

import (
	"context"
	"encoding/json"
	"regexp"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/engine"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/schema"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

type vcsOpDef struct {
	op   string
	args any
	desc string
}

var vcsDefs = []vcsOpDef{
	{sdk.VCSOpStatus, nil, "Working tree status: branch, upstream, ahead/behind, operation in progress (merge/rebase), changed files with staged/unstaged/conflicted flags."},
	{sdk.VCSOpDiff, sdk.VCSDiffArgs{}, "Diff of one file as hunks with line numbers: working tree vs HEAD (default), staged vs HEAD (staged=true), a commit vs its parent (commit), or base..head."},
	{sdk.VCSOpCommit, sdk.VCSCommitArgs{}, "Commit. paths lists files to commit (untracked files are added); empty paths commits what is staged. amend, signOff, push after commit."},
	{sdk.VCSOpPush, sdk.VCSPushArgs{}, "Push current branch (or given branch) to its upstream/remote. force uses --force-with-lease. Runs as a task."},
	{sdk.VCSOpPull, sdk.VCSPullArgs{}, "Pull from upstream. mode: merge (default) | rebase | ff-only. autostash stashes local changes first. Runs as a task."},
	{sdk.VCSOpFetch, sdk.VCSFetchArgs{}, "Fetch from a remote (all remotes when empty). Runs as a task."},
	{sdk.VCSOpBranches, nil, "Local and remote branches with upstream, ahead/behind and last commit."},
	{sdk.VCSOpCheckout, sdk.VCSCheckoutArgs{}, "Checkout a branch, remote branch (creates a tracking branch), tag or commit; create=true makes a new branch."},
	{sdk.VCSOpBranchCreate, sdk.VCSBranchCreateArgs{}, "Create a branch."},
	{sdk.VCSOpBranchDelete, sdk.VCSBranchDeleteArgs{}, "Delete a local branch, or a remote branch with remote=true."},
	{sdk.VCSOpBranchRename, sdk.VCSBranchRenameArgs{}, "Rename a branch."},
	{sdk.VCSOpSetUpstream, sdk.VCSSetUpstreamArgs{}, "Set or unset the upstream of a branch."},
	{sdk.VCSOpMerge, sdk.VCSRefArgs{}, "Merge ref into the current branch (noFF, squash, message)."},
	{sdk.VCSOpRebase, sdk.VCSRefArgs{}, "Rebase the current branch onto ref."},
	{sdk.VCSOpRebaseInteractive, sdk.VCSRebasePlan{}, "Interactive rebase with an explicit plan (pick/reword/squash/fixup/drop, reordered)."},
	{sdk.VCSOpAbort, nil, "Abort the merge/rebase/cherry-pick/revert in progress."},
	{sdk.VCSOpContinue, nil, "Continue the merge/rebase/cherry-pick/revert in progress after conflicts are resolved."},
	{sdk.VCSOpLog, sdk.VCSLogArgs{}, "Commit history with graph lanes; filter by ref (\"--all\" for all branches), author, grep, path; paginate with skip/limit."},
	{sdk.VCSOpShow, sdk.VCSShowArgs{}, "Commit detail: message body and changed files."},
	{sdk.VCSOpFileHistory, sdk.VCSFileHistoryArgs{}, "History of one file."},
	{sdk.VCSOpBlame, sdk.VCSBlameArgs{}, "Who last changed each line of a file."},
	{sdk.VCSOpCompare, sdk.VCSCompareArgs{}, "Compare two refs: commits only in head, commits only in base, changed files."},
	{sdk.VCSOpPushPreview, sdk.VCSPushArgs{}, "Commits that a push would send, and whether the remote is ahead."},
	{sdk.VCSOpDiscard, sdk.VCSPathsArgs{}, "Discard local changes of files (untracked files are deleted)."},
	{sdk.VCSOpStage, sdk.VCSPathsArgs{}, "Stage files."},
	{sdk.VCSOpUnstage, sdk.VCSPathsArgs{}, "Unstage files."},
	{sdk.VCSOpStageHunks, sdk.VCSHunksArgs{}, "Stage selected hunks of a file (indexes from vcs_diff with the same context)."},
	{sdk.VCSOpUnstageHunks, sdk.VCSHunksArgs{}, "Unstage selected hunks (indexes from the staged diff)."},
	{sdk.VCSOpDiscardHunks, sdk.VCSHunksArgs{}, "Discard selected hunks from the working tree."},
	{sdk.VCSOpUndoCommit, nil, "Undo the last commit, keeping its changes in the working tree."},
	{sdk.VCSOpReword, sdk.VCSRewordArgs{}, "Change the message of a commit."},
	{sdk.VCSOpCheckoutFile, sdk.VCSCheckoutFileArgs{}, "Restore a file to its content at a ref."},
	{sdk.VCSOpCherryPick, sdk.VCSRefArgs{}, "Cherry-pick commit(s) onto the current branch."},
	{sdk.VCSOpRevert, sdk.VCSRefArgs{}, "Revert commit(s)."},
	{sdk.VCSOpReset, sdk.VCSResetArgs{}, "Reset the current branch to a ref (soft/mixed/hard)."},
	{sdk.VCSOpStashList, nil, "List stashes."},
	{sdk.VCSOpStashSave, sdk.VCSStashSaveArgs{}, "Stash local changes (optionally including untracked)."},
	{sdk.VCSOpStashApply, sdk.VCSStashArgs{}, "Apply (or pop) a stash."},
	{sdk.VCSOpStashDrop, sdk.VCSStashArgs{}, "Drop a stash."},
	{sdk.VCSOpResolve, sdk.VCSResolveArgs{}, "Resolve a conflicted file: side=ours|theirs, or resolved after manual merge."},
	{sdk.VCSOpMergeTool, sdk.VCSPathsArgs{}, "Open conflicted files in the external merge tool."},
	{sdk.VCSOpTags, nil, "List tags."},
	{sdk.VCSOpTagCreate, sdk.VCSTagCreateArgs{}, "Create a tag (annotated when message is given)."},
	{sdk.VCSOpTagDelete, sdk.VCSRefArgs{}, "Delete a tag."},
	{sdk.VCSOpRemotes, nil, "List remotes."},
	{sdk.VCSOpRemoteSet, sdk.VCSRemote{}, "Add a remote or change its URL."},
	{sdk.VCSOpRemoteRemove, sdk.VCSRefArgs{}, "Remove a remote."},
	{sdk.VCSOpSubmodules, nil, "List submodules."},
	{sdk.VCSOpSubmoduleUpdate, sdk.VCSSubmoduleUpdateArgs{}, "Init/update submodules. Runs as a task."},
	{sdk.VCSOpWorktrees, nil, "List worktrees."},
	{sdk.VCSOpWorktreeAdd, sdk.VCSWorktreeAddArgs{}, "Add a worktree."},
	{sdk.VCSOpWorktreeRemove, sdk.VCSWorktreeRemoveArgs{}, "Remove a worktree."},
	{sdk.VCSOpConfigGet, sdk.VCSConfigGetArgs{}, "Read git config values (e.g. user.name, user.email, pull.rebase)."},
	{sdk.VCSOpConfigSet, sdk.VCSConfigSetArgs{}, "Set git config (repository or global)."},
	{sdk.VCSOpIgnore, sdk.VCSIgnoreArgs{}, "Append patterns to .gitignore (or .git/info/exclude)."},
	{sdk.VCSOpClean, sdk.VCSPathsArgs{}, "Delete untracked files."},
	{sdk.VCSOpInit, nil, "Initialize a repository in the project folder."},
	{sdk.VCSOpSkip, nil, "Skip the current commit of a rebase/cherry-pick in progress."},
	{sdk.VCSOpFileContent, sdk.VCSFileContentArgs{}, "Content of a file at a ref (empty = working tree, \":\" = index); text or base64 for binary."},
	{sdk.VCSOpContainingBranches, sdk.VCSShowArgs{}, "Branches and tags that contain a commit."},
	{sdk.VCSOpTagPush, sdk.VCSRefArgs{}, "Push (or delete with delete=true) a tag on a remote."},
	{sdk.VCSOpMergeTools, nil, "External merge tools detected on this machine."},
	{sdk.VCSOpStashShow, sdk.VCSStashArgs{}, "Files changed in a stash."},
}

func (r *Registry) registerVCS() {
	for _, d := range vcsDefs {
		d := d
		s := schema.Of(d.args)
		props, _ := s["properties"].(map[string]any)
		if props == nil {
			props = map[string]any{}
			s["properties"] = props
		}
		props["projectId"] = map[string]any{"type": "string", "description": "Project id."}
		props["root"] = map[string]any{"type": "string", "description": "Repository path when the project has several (see vcs_roots); default is the main repository."}
		req, _ := s["required"].([]string)
		s["required"] = append([]string{"projectId"}, req...)
		op := &Op{
			Name: "vcs." + d.op, Title: "Git " + d.op, Description: d.desc,
			ReadOnly: engine.VCSReadOnly(d.op), Destructive: engine.VCSDestructive(d.op), InputSchema: s,
		}
		op.handler = func(ctx context.Context, raw json.RawMessage) (any, error) {
			var m map[string]json.RawMessage
			if len(raw) > 0 {
				if err := json.Unmarshal(raw, &m); err != nil {
					return nil, &BadRequest{Msg: err.Error()}
				}
			}
			var pid, root string
			_ = json.Unmarshal(m["projectId"], &pid)
			_ = json.Unmarshal(m["root"], &root)
			delete(m, "projectId")
			delete(m, "root")
			args, _ := json.Marshal(m)
			return r.e.VCSCall(ctx, pid, root, d.op, args)
		}
		r.add(op)
	}
}

func compileGrep(s string) (*regexp.Regexp, error) { return regexp.Compile("(?i)" + s) }
