package main

import (
	"errors"
	"os"
)

type diskSpace struct {
	Available  uint64  `json:"availableBytes"`
	Total      uint64  `json:"totalBytes"`
	FreeInodes *uint64 `json:"freeInodes,omitempty"`
}

func fileBytes(path string) *int64 {
	info, err := os.Stat(path)
	var size int64
	if err == nil {
		size = info.Size()
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return &size
}
