//go:build !linux && !windows

package main

func dataDisk(string) *diskSpace { return nil }
