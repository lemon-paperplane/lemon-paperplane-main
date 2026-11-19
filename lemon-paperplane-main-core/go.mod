module github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core

go 1.23

require (
	github.com/creack/pty v1.1.24
	github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk v0.0.0
	golang.org/x/text v0.21.0
)

require (
	golang.org/x/crypto v0.32.0 // indirect
	golang.org/x/sys v0.29.0 // indirect
	golang.org/x/term v0.28.0 // indirect
)

replace github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk => ../lemon-paperplane-main-plugin-sdk
