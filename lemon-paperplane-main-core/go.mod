module github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core

go 1.23

require (
	github.com/creack/pty v1.1.24
	github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk v0.0.0
	golang.org/x/text v0.21.0
)

replace github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk => ../lemon-paperplane-main-plugin-sdk
