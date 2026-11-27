package main

import "golang.org/x/sys/unix"

func dataDisk(path string) *diskSpace {
	var fs unix.Statfs_t
	if unix.Statfs(path, &fs) != nil {
		return nil
	}
	space := &diskSpace{Available: fs.Bavail * uint64(fs.Bsize), Total: fs.Blocks * uint64(fs.Bsize)}
	if fs.Files > 0 {
		space.FreeInodes = &fs.Ffree
	}
	return space
}
