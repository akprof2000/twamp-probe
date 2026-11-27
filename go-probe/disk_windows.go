package main

import "golang.org/x/sys/windows"

func dataDisk(path string) *diskSpace {
	p, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil
	}
	var space diskSpace
	if windows.GetDiskFreeSpaceEx(p, &space.Available, &space.Total, nil) != nil {
		return nil
	}
	return &space
}
